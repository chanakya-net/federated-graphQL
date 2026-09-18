using HotChocolate.Authorization;

namespace SoR.SoftwareInstall.GraphQL;

/// <summary>A valid token is required for everything (AUTH_NOT_AUTHENTICATED otherwise); no service check here.</summary>
[Authorize]
public sealed class Query
{
    // Internal lookup for the gateway only (docs/version-facts.md §2): not on the composite schema.
    // Always a stub, never null: tenant scoping happens in the field resolver, keyed on the token's tenant claim.
    // Returning null here would make installEvents null WITHOUT an error and break the UI contract.
    // The return type stays nullable (`Device`, as in the contract); the value never is.
    [Lookup, Internal]
    [GraphQLDescription("Internal lookup for the gateway only. Tenant-scoped, NOT behind the service policy.")]
    public Device? GetDeviceById([ID] string id) => new(id);
}
