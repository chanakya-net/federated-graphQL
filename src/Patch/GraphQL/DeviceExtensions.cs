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

/// <summary>
/// <c>matches</c> is resolved only when selected: the sets are the expensive part of the reverse lookup and most
/// pages do not need them. The tenant comes from the token, the selection from the parent.
/// </summary>
[ExtendObjectType<PatchDeviceMatches>]
public sealed class PatchDeviceMatchesExtensions
{
    [GraphQLDescription(
        "Per selected patch (unknown ids included, with an empty set), the ids of the caller's devices with an event for it, " +
        "sorted, at most 10000: lets a client combine selections with AND / OR, also across subgraphs. Resolved only when selected.")]
    public async Task<IReadOnlyList<PatchMatch>> GetMatches(
        [Parent] PatchDeviceMatches parent,
        [Service] ICallerContext caller,
        [Service] IPatchStore store,
        CancellationToken ct)
        => await store.GetMatchesAsync(caller.TenantId, parent.PatchIds, ct);
}
