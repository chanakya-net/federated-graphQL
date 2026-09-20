using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SoR.DeviceSearch.GraphQL;
using SoR.DeviceSearch.Search;
using SoR.DeviceSearch.Providers;
using static SoR.DeviceSearch.Providers.SearchResponse;

namespace SoR.DeviceSearch.Transport;

/// <summary>Only direct, configured domain endpoints. JWT is copied to each message, never default client headers.</summary>
public sealed class DomainSearchClient(HttpClient http, IHttpContextAccessor accessor, IConfiguration configuration, SearchProviderRegistry registry) : IDomainSearchClient
{
    public const int DomainPageSize = 100;
    public const int MaxIdsPerFilter = 50_000;
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    public const int MaxEventsPerResponse = 50_000;

    public async Task<IReadOnlySet<string>> DiscoverAsync(DeviceSearchFilterInput filter, CancellationToken ct)
    {
        var provider = registry.Resolve(filter.Category);
        var key = provider.NormalizeKey(filter.Key);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int? expected = null;
        while (true)
        {
            var result = await RequestAsync(provider, provider.SearchRequest(key, null, DomainPageSize, ids.Count, false), ct);
            var total = Total(result);
            if (total > MaxIdsPerFilter) throw DeviceSearchEngine.Error("SEARCH_LIMIT_EXCEEDED", $"{filter.Category} exceeds {MaxIdsPerFilter} device IDs for one filter. Narrow the filters.");
            if (expected is not null && total != expected) throw Malformed(filter.Category, "source count changed while paging; retry");
            expected = total;
            var items = Items(result);
            if (items.GetArrayLength() != Math.Min(DomainPageSize, total - ids.Count))
                throw Malformed(filter.Category, "incomplete discovery page");
            foreach (var item in items.EnumerateArray())
            {
                if (!ids.Add(DeviceId(item))) throw Malformed(filter.Category, "duplicate device ID while paging");
            }
            if (ids.Count == total) return ids;
        }
    }

    public async Task<IReadOnlyList<DeviceSearchItem>> DetailsAsync(DeviceSearchFilterInput filter, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var provider = registry.Resolve(filter.Category);
        var key = provider.NormalizeKey(filter.Key);
        if (ids.Count == 0) return [];
        if (ids.Count > DomainPageSize) throw DeviceSearchEngine.Error("SEARCH_LIMIT_EXCEEDED", "Details accept at most 100 device IDs.");
        var result = await RequestAsync(provider, provider.SearchRequest(key, ids, DomainPageSize, 0, true), ct);
        var items = Items(result);
        if (Total(result) != ids.Count || items.GetArrayLength() != ids.Count)
            throw Malformed(filter.Category, "selected devices changed or details are incomplete; retry");
        var remaining = ids.ToHashSet(StringComparer.Ordinal);
        var output = new List<DeviceSearchItem>();
        var eventCount = 0;
        foreach (var item in items.EnumerateArray())
        {
            var id = DeviceId(item);
            if (!remaining.Remove(id)) throw Malformed(filter.Category, "unexpected or duplicate device in details");
            var rawEvents = Property(item, "events");
            if (rawEvents.ValueKind != JsonValueKind.Array) throw Malformed(filter.Category, "missing events");
            eventCount += rawEvents.GetArrayLength();
            if (eventCount > MaxEventsPerResponse) throw DeviceSearchEngine.Error("SEARCH_LIMIT_EXCEEDED", "Too many matching events. Narrow the filters.");
            var events = rawEvents.EnumerateArray().Select(e => provider.MapEvent(key, e)).ToArray();
            if (events.Length == 0) throw Malformed(filter.Category, "matching device has no matching events; retry");
            output.Add(new(new Device(id), events));
        }
        return output;
    }

    public async Task<IReadOnlyList<SearchCatalogItem>> CatalogAsync(string category, string? search, int first, CancellationToken ct)
    {
        if (first is < 1 or > 100) throw DeviceSearchEngine.Error("BAD_USER_INPUT", "Catalog first must be between 1 and 100.");
        var provider = registry.Resolve(category);
        var result = await RequestAsync(provider, provider.CatalogRequest(search, first), ct);
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > first)
            throw Malformed(category, "invalid or oversized catalog");
        // Validate the entire bounded response before returning anything, including options beyond the UI cap.
        var options = result.EnumerateArray().SelectMany(provider.MapCatalogItem).ToArray();
        return options.DistinctBy(item => item.Key, StringComparer.Ordinal).Take(first).ToArray();
    }

    private async Task<JsonElement> RequestAsync(ISearchProvider provider, SearchSourceRequest source, CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration[source.EndpointSetting], UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw DeviceSearchEngine.Error("SEARCH_SOURCE_FAILED", $"{provider.Category} endpoint is not configured.");
        var authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!AuthenticationHeaderValue.TryParse(authorization, out var bearer) || !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(bearer.Parameter))
            throw DeviceSearchEngine.Error("AUTH_NOT_AUTHENTICATED", "A caller bearer token is required.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { query = source.Query, variables = source.Variables }),
        };
        request.Headers.Authorization = bearer;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var seconds = int.TryParse(configuration["SUBGRAPH_TIMEOUT_SECONDS"], out var configured) ? Math.Clamp(configured, 1, 20) : 5;
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw DeviceSearchEngine.Error("SEARCH_SOURCE_FAILED", $"{provider.Category} returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw DeviceSearchEngine.Error("SEARCH_LIMIT_EXCEEDED", $"{provider.Category} response exceeds the search size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) throw DeviceSearchEngine.Error("SEARCH_LIMIT_EXCEEDED", $"{provider.Category} response exceeds the search size limit.");
                buffer.Write(bytes, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var responseBody = document.RootElement;
            if (responseBody.TryGetProperty("errors", out var errors) && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0))
                throw DeviceSearchEngine.Error("SEARCH_SOURCE_FAILED", $"{provider.Category} returned GraphQL errors. Search cannot return partial results.");
            var result = Property(Property(responseBody, "data"), "result");
            if (result.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw Malformed(provider.Category, "missing result");
            return result.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw DeviceSearchEngine.Error("SEARCH_SOURCE_TIMEOUT", $"{provider.Category} request timed out.");
        }
        catch (HttpRequestException)
        {
            throw DeviceSearchEngine.Error("SEARCH_SOURCE_FAILED", $"{provider.Category} is unavailable.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            throw Malformed(provider.Category, "invalid response");
        }
    }

    private static int Total(JsonElement result)
    {
        var total = Property(result, "totalCount");
        if (total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out var count) || count < 0) throw Malformed("Domain", "invalid totalCount");
        return count;
    }
    private static JsonElement Items(JsonElement result)
    {
        var items = Property(result, "items");
        if (items.ValueKind != JsonValueKind.Array) throw Malformed("Domain", "missing items");
        return items;
    }
    private static string DeviceId(JsonElement item) => Text(Property(item, "device"), "id");
}
