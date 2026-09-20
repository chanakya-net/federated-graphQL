using System.Text.Json;
using SoR.DeviceSearch.GraphQL;

namespace SoR.DeviceSearch.Providers;

/// <summary>Trusted adapter: owns domain semantics; the transport owns bounded HTTP and paging.</summary>
public interface ISearchProvider
{
    string Category { get; }
    string RequiredPermission { get; }
    SearchCapability Capability(bool available);
    string NormalizeKey(string key);
    SearchSourceRequest SearchRequest(string key, IReadOnlyList<string>? ids, int first, int offset, bool details);
    DeviceSearchEvent MapEvent(string key, JsonElement source);
    SearchSourceRequest CatalogRequest(string? search, int first);
    IEnumerable<SearchCatalogItem> MapCatalogItem(JsonElement source);
}

public sealed record SearchSourceRequest(string EndpointSetting, string Query, object Variables);
