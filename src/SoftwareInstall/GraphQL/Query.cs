using HotChocolate.Authorization;
using HotChocolate.Resolvers;
using SoR.Shared.Auth;
using SoR.SoftwareInstall.Storage;

namespace SoR.SoftwareInstall.GraphQL;

/// <summary>
/// A valid token is required for everything (AUTH_NOT_AUTHENTICATED otherwise). The service policy guards the data
/// fields (<c>software</c>, <c>devicesWithSoftware</c>, and <c>installEvents</c> on <see cref="DeviceExtensions"/>),
/// not the lookup.
/// </summary>
[Authorize]
public sealed class Query
{
    public const int DefaultFirst = 25;
    public const int MaxFirst = 100;

    /// <summary>Upper bound on the keys one reverse lookup may select; more is a field error, never a silent cut.</summary>
    public const int MaxSelection = 50;

    /// <summary>Blob reads for one page run this many at a time (a page is at most <see cref="MaxFirst"/> devices).</summary>
    public const int MaxConcurrentReads = 16;

    // Internal lookup for the gateway only (docs/version-facts.md §2): not on the composite schema.
    // Always a stub, never null: tenant scoping happens in the field resolver, keyed on the token's tenant claim.
    // Returning null here would make installEvents null WITHOUT an error and break the UI contract.
    // The return type stays nullable (`Device`, as in the contract); the value never is.
    [Lookup, Internal]
    [GraphQLDescription("Internal lookup for the gateway only. Tenant-scoped, NOT behind the service policy.")]
    public Device? GetDeviceById([ID] string id) => new(id);

    /// <summary>Nullable on purpose (contract): a denial on a non-null root field would null all of <c>data</c>.</summary>
    [Authorize(Policy = DevAuth.ServiceAccessPolicy)]
    [GraphQLDescription(
        "Software catalog: every distinct name / version / publisher seen in any install event, ordered by name then version. " +
        "`search` matches name or publisher (case-insensitive substring). `first` is clamped to 1..100. Requires services contains \"softwareinstall\".")]
    public async Task<IReadOnlyList<Software>?> GetSoftware(
        string? search,
        [Service] ISoftwareIndexStore index,
        CancellationToken ct,
        int first = DefaultFirst,
        int offset = 0)
        => (await index.GetAsync(ct)).Search(search, Math.Clamp(first, 1, MaxFirst), Math.Max(0, offset));

    /// <summary>
    /// Reverse lookup: from software to the devices with install events for it. The candidates come from the index
    /// blob; the events of the page come from each device's own blob (the system of record). The tenant comes from the
    /// token only. Nullable like <c>software</c>: a denial or an outage nulls this field and its error stays at
    /// <c>["devicesWithSoftware"]</c>.
    /// </summary>
    [Authorize(Policy = DevAuth.ServiceAccessPolicy)]
    [GraphQLDescription(
        "Devices in the caller's tenant with at least one install event for any of `software` (at most 50 keys; a key without a " +
        "version matches every version), ordered by device id, each with its events for that software newest first. `deviceIds` " +
        "(at most 100) restricts the candidates, e.g. to the page a client computed from `matches`. `first` is clamped to 1..100. " +
        "Requires services contains \"softwareinstall\". Empty `items` = no device matches; null + error = degraded or denied.")]
    public async Task<SoftwareDeviceMatches?> GetDevicesWithSoftware(
        IReadOnlyList<SoftwareKeyInput> software,
        [ID] IReadOnlyList<string>? deviceIds,
        [Service] ICallerContext caller,
        [Service] ISoftwareIndexStore index,
        [Service] IInstallEventsStore store,
        IResolverContext context,
        CancellationToken ct,
        int first = DefaultFirst,
        int offset = 0)
    {
        var keys = software
            .Where(k => !string.IsNullOrWhiteSpace(k.Name))
            .Select(k => new SoftwareKey(k.Name.Trim(), string.IsNullOrWhiteSpace(k.Version) ? null : k.Version.Trim()))
            .Distinct()
            .ToList();
        if (keys.Count > MaxSelection) throw TooLarge("software", "keys", MaxSelection, keys.Count);
        var candidates = deviceIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (candidates is { Count: > MaxFirst }) throw TooLarge("deviceIds", "ids", MaxFirst, candidates.Count);

        if (keys.Count == 0 || candidates is { Count: 0 }) return new SoftwareDeviceMatches([], 0) { Keys = keys };

        var tenantId = caller.TenantId;
        IReadOnlyList<string> all = (await index.GetAsync(ct)).DevicesFor(tenantId, keys);
        if (candidates is not null)
        {
            var wanted = candidates.ToHashSet(StringComparer.Ordinal);
            all = [.. all.Where(wanted.Contains)];
        }

        var page = all.Skip(Math.Max(0, offset)).Take(Math.Clamp(first, 1, MaxFirst)).ToList();
        if (page.Count == 0) return new SoftwareDeviceMatches([], all.Count) { Keys = keys };

        // Discovery and counts need only the index; aliases/fragments are handled by the selection API.
        if (!context.Select("items").IsSelected("events"))
            return new SoftwareDeviceMatches([.. page.Select(id => new SoftwareDeviceMatch(new Device(id), []))], all.Count) { Keys = keys };

        // The page's blobs, a bounded number at a time. A device the index knows but whose blob is gone has no events.
        var events = new IReadOnlyList<InstallEvent>[page.Count];
        await Parallel.ForAsync(0, page.Count, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentReads, CancellationToken = ct },
            async (i, token) =>
            {
                var document = await store.ReadAsync(tenantId, page[i], token);
                events[i] = document is null
                    ? []
                    : [.. document.Events
                        .Where(e => SoftwareIndex.Matches(e.Software, keys))
                        .OrderByDescending(e => e.OccurredAt)
                        .ThenByDescending(e => e.Id, StringComparer.Ordinal)
                        .Select(e => new InstallEvent(e.Id, document.DeviceId, e.OccurredAt, e.Action, e.Result, e.Software))];
            });

        var items = page.Select((id, i) => new SoftwareDeviceMatch(new Device(id), events[i])).ToList();
        return new SoftwareDeviceMatches(items, all.Count) { Keys = keys };
    }

    /// <summary>More than the cap is a field error, never a silent cut.</summary>
    private static GraphQLException TooLarge(string argument, string noun, int max, int got) =>
        new(ErrorBuilder.New()
            .SetMessage($"{argument}: at most {max} {noun} per query, got {got}.")
            .SetCode("SELECTION_TOO_LARGE")
            .Build());
}
