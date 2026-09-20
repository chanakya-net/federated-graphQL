using SoR.DeviceSearch.GraphQL;

namespace SoR.DeviceSearch.Transport;

public interface IDomainSearchClient
{
    Task<IReadOnlySet<string>> DiscoverAsync(DeviceSearchFilterInput filter, CancellationToken ct);
    Task<IReadOnlyList<DeviceSearchItem>> DetailsAsync(DeviceSearchFilterInput filter, IReadOnlyList<string> ids, CancellationToken ct);
    Task<IReadOnlyList<SearchCatalogItem>> CatalogAsync(string category, string? search, int first, CancellationToken ct);
}
