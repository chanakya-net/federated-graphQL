using HotChocolate.Authorization;
using SoR.DeviceSearch.Search;
using SoR.DeviceSearch.Providers;
using SoR.Shared.Auth;

namespace SoR.DeviceSearch.GraphQL;

[Authorize]
public sealed class Query
{
    public async Task<FindDevicesResult?> FindDevices(
        IReadOnlyList<DeviceSearchFilterInput> filters,
        [Service] DeviceSearchEngine search,
        CancellationToken ct,
        int first = 25,
        int offset = 0) => await search.FindAsync(filters, first, offset, ct);

    public IReadOnlyList<SearchCapability> SearchCapabilities(
        [Service] SearchProviderRegistry registry, [Service] ICallerContext caller) =>
        registry.Providers.Select(p => p.Capability(caller.HasService(p.RequiredPermission))).ToArray();

    public Task<IReadOnlyList<SearchCatalogItem>?> SearchCatalog(
        string category, string? search, [Service] DeviceSearchEngine engine,
        CancellationToken ct, int first = 25) => engine.CatalogAsync(category, search, first, ct);
}
