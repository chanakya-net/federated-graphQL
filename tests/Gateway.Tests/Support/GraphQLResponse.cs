using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SoR.Gateway.Tests.Support;

public sealed record GraphQLResponse(HttpStatusCode Status, JsonElement Root, string Body, TimeSpan Elapsed)
{
    public JsonElement Data => Root.GetProperty("data");

    public bool HasErrors => Root.TryGetProperty("errors", out _);

    public IReadOnlyList<JsonElement> Errors =>
        Root.TryGetProperty("errors", out var errors) ? [.. errors.EnumerateArray()] : [];

    /// <summary>Errors whose path starts with <paramref name="prefix"/> (the UI's matching rule, contracts/errors.md).</summary>
    public IReadOnlyList<JsonElement> ErrorsAt(params object[] prefix) =>
        [.. Errors.Where(e => e.TryGetProperty("path", out var path) && StartsWith(path, prefix))];

    public static async Task<GraphQLResponse> PostAsync(HttpClient client, string requestUri, string query, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = JsonContent.Create(new { query }) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        sw.Stop();
        using var doc = JsonDocument.Parse(body);
        return new GraphQLResponse(response.StatusCode, doc.RootElement.Clone(), body, sw.Elapsed);
    }

    public override string ToString() => $"{(int)Status} {Body}";

    private static bool StartsWith(JsonElement path, object[] prefix)
    {
        var segments = path.EnumerateArray().ToList();
        if (segments.Count < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            var matches = prefix[i] switch
            {
                string s => segments[i].ValueKind == JsonValueKind.String && segments[i].GetString() == s,
                int n => segments[i].ValueKind == JsonValueKind.Number && segments[i].GetInt32() == n,
                _ => false,
            };
            if (!matches) return false;
        }

        return true;
    }
}
