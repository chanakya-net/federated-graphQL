using HotChocolate.Authorization;
using HotChocolate.Resolvers;
using HotChocolate.Types.Composite;
using SoR.Patch.Data;
using SoR.Shared.Auth;

namespace SoR.Patch.GraphQL;

/// <summary>Every field requires a valid token (AUTH_NOT_AUTHENTICATED otherwise).</summary>
[Authorize]
public sealed class Query
{
    public const int DefaultFirst = 25;
    public const int MaxFirst = 100;

    /// <summary>Upper bound on the ids one reverse lookup may select; more is a field error, never a silent cut.</summary>
    public const int MaxSelection = 50;

    /// <summary>
    /// Internal lookup (docs/version-facts.md §2): only the gateway calls it; it is not on the composite schema.
    /// Always a stub, never null (the contract type is nullable, the value never is): tenant scoping happens in
    /// <see cref="DeviceExtensions.GetPatchEvents"/>, keyed on the token. Returning null here would make
    /// <c>patchEvents</c> null WITHOUT an error and break the UI's error contract.
    /// </summary>
    [Lookup]
    [Internal]
    [GraphQLDescription("Internal lookup for the gateway only. Tenant-scoped, NOT behind the service policy. Always returns a stub Device.")]
    public Device? GetDeviceById([ID] string id) => new(id);

    /// <summary>Guarded by the service policy; nullable so a denial nulls only this field (contracts/errors.md).</summary>
    [Authorize(Policy = DevAuth.ServiceAccessPolicy)]
    [GraphQLDescription(
        "Patch catalog ordered by id. `search` matches kbId or title (case-insensitive substring). " +
        "Requires services contains \"patch\". `first` is clamped to 1..100.")]
    public async Task<IReadOnlyList<PatchInfo>?> GetPatches(
        string? search,
        [Service] IPatchStore store,
        CancellationToken ct,
        int first = DefaultFirst,
        int offset = 0)
        => await store.GetPatchesAsync(search, Math.Clamp(first, 1, MaxFirst), Math.Max(0, offset), ct);

    /// <summary>
    /// Reverse lookup: from patches to the devices that have events for them. The tenant comes from the token only,
    /// so another tenant's devices never match. Nullable for the same reason as <c>patches</c>: a denial or an
    /// outage nulls this field and its error stays at <c>["devicesWithPatches"]</c>.
    /// </summary>
    [Authorize(Policy = DevAuth.ServiceAccessPolicy)]
    [GraphQLDescription(
        "Devices in the caller's tenant with at least one event for any of `patchIds` (at most 50 ids), ordered by device id, " +
        "each with its events for those patches newest first. `deviceIds` (at most 100) restricts the candidates, e.g. to the " +
        "page a client computed from `matches`. `first` is clamped to 1..100. Requires services contains \"patch\". " +
        "Empty `items` = no device matches; null + error = degraded or denied.")]
    public async Task<PatchDeviceMatches?> GetDevicesWithPatches(
        [ID] IReadOnlyList<string> patchIds,
        [ID] IReadOnlyList<string>? deviceIds,
        [Service] ICallerContext caller,
        [Service] IPatchStore store,
        IResolverContext context,
        CancellationToken ct,
        int first = DefaultFirst,
        int offset = 0)
    {
        var ids = Selection(patchIds, "patchIds", MaxSelection);
        var devices = deviceIds is null ? null : Selection(deviceIds, "deviceIds", MaxFirst);
        if (ids.Count == 0 || devices is { Count: 0 }) return new PatchDeviceMatches([], 0) { PatchIds = ids };

        var page = await store.FindDevicesAsync(caller.TenantId, ids, devices, Math.Clamp(first, 1, MaxFirst), Math.Max(0, offset), ct, context.Select("items").IsSelected("events"));
        return page with { PatchIds = ids };
    }

    /// <summary>Blank ids dropped, duplicates collapsed; more than <paramref name="max"/> is a field error, never a silent cut.</summary>
    internal static List<string> Selection(IReadOnlyList<string> ids, string argument, int max)
    {
        var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count > max)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage($"{argument}: at most {max} ids per query, got {list.Count}.")
                .SetCode("SELECTION_TOO_LARGE")
                .Build());
        }

        return list;
    }
}
