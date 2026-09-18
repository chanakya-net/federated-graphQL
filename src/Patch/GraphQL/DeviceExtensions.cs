using HotChocolate.Authorization;
using SoR.Patch.Data;
using SoR.Shared.Auth;

namespace SoR.Patch.GraphQL;

[ExtendObjectType<Device>]
public sealed class DeviceExtensions
{
    /// <summary>
    /// NULLABLE list on purpose (plan §4.2, contracts/patch.graphqls). Do not change to non-null: a denial or
    /// outage on a non-null field would null the whole Device. Schema test PatchEvents_field_is_nullable_list.
    /// The tenant comes from the token only: another tenant's device yields [], never rows and never an error.
    /// </summary>
    [Authorize(Policy = DevAuth.ServiceAccessPolicy)]
    [GraphQLDescription(
        "Patch events of this device, newest first, at most 1000. `since` / `until` are inclusive. " +
        "Requires services contains \"patch\". Empty list = no events; null + error = degraded or denied.")]
    public async Task<IReadOnlyList<PatchEvent>?> GetPatchEvents(
        [Parent] Device device,
        DateTimeOffset? since,
        DateTimeOffset? until,
        [Service] ICallerContext caller,
        [Service] IPatchStore store,
        CancellationToken ct)
        => await store.GetEventsAsync(caller.TenantId, device.Id, since, until, ct);
}
