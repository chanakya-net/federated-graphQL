using SoR.DeviceSearch.Search;

namespace SoR.DeviceSearch.Providers;

public sealed class SearchProviderRegistry
{
    private readonly IReadOnlyDictionary<string, ISearchProvider> byCategory;
    public IReadOnlyList<ISearchProvider> Providers { get; }

    public SearchProviderRegistry(IEnumerable<ISearchProvider> providers)
    {
        Providers = providers.ToArray();
        var map = new Dictionary<string, ISearchProvider>(StringComparer.Ordinal);
        foreach (var provider in Providers)
            if (!map.TryAdd(provider.Category, provider))
                throw new InvalidOperationException($"Duplicate search provider category: {provider.Category}.");
        byCategory = map;
    }

    public ISearchProvider Resolve(string category) =>
        byCategory.TryGetValue(category, out var provider) ? provider
            : throw DeviceSearchEngine.Error("BAD_USER_INPUT", $"Unknown search category: {category}.");
}
