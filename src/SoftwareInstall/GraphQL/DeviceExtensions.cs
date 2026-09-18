using HotChocolate.Authorization;
using SoR.Shared.Auth;
using SoR.SoftwareInstall.Storage;

namespace SoR.SoftwareInstall.GraphQL;

[ExtendObjectType<Device>]
public sealed class DeviceExtensions
{
    /// <summary>Upper bound on events returned per device (seeded devices have 3..20).</summary>
    public const int MaxEvents = 1_000;

    // NULLABLE list on purpose (plan §4.2): a denial or outage here must not null out the parent Device.
    // Do not change to non-null. SchemaTests.InstallEvents_field_is_nullable_list enforces this.
    [Authorize(Policy = DevAuth.ServiceAccessPolicy)]
    [GraphQLDescription(
        "NULLABLE on purpose (plan §4.2). Requires services contains \"softwareinstall\". " +
        "Empty list = no events; null + error = degraded or denied. Never make this non-null.")]
    public async Task<IReadOnlyList<InstallEvent>?> GetInstallEvents(
        [Parent] Device device,
        DateTimeOffset? since,
        DateTimeOffset? until,
        [Service] ICallerContext caller,
        [Service] IInstallEventsStore store,
        CancellationToken ct)
    {
        // The tenant comes from the token only. Another tenant's device has no blob under this prefix -> [].
        var document = await store.ReadAsync(caller.TenantId, device.Id, ct);
        if (document is null) return [];

        return document.Events
            .Where(e => (since is null || e.OccurredAt >= since) && (until is null || e.OccurredAt <= until))
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Take(MaxEvents)
            .Select(e => new InstallEvent(e.Id, document.DeviceId, e.OccurredAt, e.Action, e.Result, e.Software))
            .ToList();
    }
}
