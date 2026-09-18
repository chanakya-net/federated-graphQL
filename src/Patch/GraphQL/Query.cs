using HotChocolate.Authorization;
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
    [GraphQLDescription("Patch catalog ordered by id. Requires services contains \"patch\". `first` is clamped to 1..100.")]
    public async Task<IReadOnlyList<PatchInfo>?> GetPatches(
        [Service] IPatchStore store,
        CancellationToken ct,
        int first = DefaultFirst,
        int offset = 0)
        => await store.GetPatchesAsync(Math.Clamp(first, 1, MaxFirst), Math.Max(0, offset), ct);
}
