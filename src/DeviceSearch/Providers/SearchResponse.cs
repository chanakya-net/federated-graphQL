using System.Text.Json;
using SoR.DeviceSearch.Search;

namespace SoR.DeviceSearch.Providers;

internal static class SearchResponse
{
    internal static JsonElement Property(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) ? value : throw Malformed("Domain", $"missing {property}");
    internal static string Text(JsonElement item, string property)
    {
        var value = Property(item, property);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw Malformed("Domain", $"invalid {property}");
    }
    internal static DateTimeOffset Timestamp(JsonElement item)
    {
        var timestamp = Property(item, "occurredAt");
        return timestamp.ValueKind == JsonValueKind.String && timestamp.TryGetDateTimeOffset(out var result)
            ? result : throw Malformed("Domain", "invalid event timestamp");
    }
    internal static GraphQLException Malformed(string category, string reason) =>
        DeviceSearchEngine.Error("SEARCH_SOURCE_INVALID", $"{category}: {reason}. Search cannot return partial results.");
}
