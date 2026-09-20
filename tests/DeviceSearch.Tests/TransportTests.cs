using SoR.DeviceSearch.Providers;
using System.Net;
using System.Text.Json;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SoR.DeviceSearch.GraphQL;
using SoR.DeviceSearch.Transport;

namespace SoR.DeviceSearch.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task Discovery_reads_all_pages_without_using_capped_matches_or_requesting_events()
    {
        var requests = new List<JsonElement>();
        using var handler = new Handler(async request =>
        {
            var body = await Body(request);
            requests.Add(body);
            var offset = body.GetProperty("variables").GetProperty("offset").GetInt32();
            return Json(new { data = new { result = new { totalCount = 205, items = Enumerable.Range(offset, Math.Min(100, 205 - offset)).Select(i => new { device = new { id = $"d{i:000}" } }) } } });
        });
        var domain = Client(handler);
        var result = await domain.DiscoverAsync(new("patch", "p"), default);
        Assert.Equal(205, result.Count);
        Assert.Contains("d204", result);
        Assert.Equal([0, 100, 200], requests.Select(r => r.GetProperty("variables").GetProperty("offset").GetInt32()));
        Assert.All(requests, r =>
        {
            var query = r.GetProperty("query").GetString()!;
            Assert.DoesNotContain("matches", query);
            Assert.DoesNotContain("events", query);
            Assert.Contains("device { id }", query);
        });
    }

    [Fact]
    public async Task Caller_authorization_is_attached_to_each_request_and_only_configured_domain_urls_are_used()
    {
        var seen = new List<(string? Token, string? Host)>();
        using var handler = new Handler(request =>
        {
            seen.Add((request.Headers.Authorization?.ToString(), request.RequestUri?.Host));
            return Task.FromResult(Json(new { data = new { result = new { totalCount = 0, items = Array.Empty<object>() } } }));
        });
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var domain = Client(handler, accessor);
        accessor.HttpContext.Request.Headers.Authorization = "Bearer tenant-a-token";
        await domain.DiscoverAsync(new("patch", "p"), default);
        accessor.HttpContext.Request.Headers.Authorization = "Bearer tenant-b-token";
        await domain.DiscoverAsync(new("vulnerability", "c"), default);
        Assert.Equal([("Bearer tenant-a-token", "patch.test"), ("Bearer tenant-b-token", "vuln.test")], seen);
    }

    [Theory]
    [InlineData("{\"errors\":[{\"message\":\"denied\"}],\"data\":{\"result\":null}}")]
    [InlineData("{\"data\":{\"result\":null}}")]
    [InlineData("{\"data\":{\"result\":{\"totalCount\":1,\"items\":[]}}}")]
    [InlineData("{\"data\":{\"result\":{\"totalCount\":0,\"items\":[{\"device\":{\"id\":\"x\"}}]}}}")]
    [InlineData("{\"data\":{\"result\":{\"totalCount\":2,\"items\":[{\"device\":{\"id\":\"x\"}},{\"device\":{\"id\":\"x\"}}]}}}")]
    [InlineData("{\"data\":{\"result\":{\"totalCount\":50001,\"items\":[]}}}")]
    [InlineData("not json")]
    public async Task Failed_truncated_malformed_or_over_limit_source_is_an_explicit_error(string json)
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var error = await Assert.ThrowsAsync<GraphQLException>(() => Client(handler).DiscoverAsync(new("patch", "p"), default));
        Assert.StartsWith("SEARCH_", Assert.Single(error.Errors).Code);
    }

    [Fact]
    public async Task Http_outage_fails_the_search()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var error = await Assert.ThrowsAsync<GraphQLException>(() => Client(handler).DiscoverAsync(new("patch", "p"), default));
        Assert.Equal("SEARCH_SOURCE_FAILED", Assert.Single(error.Errors).Code);
    }

    [Fact]
    public async Task Software_any_version_details_keep_selected_item_key_and_only_request_page_devices()
    {
        using var handler = new Handler(async request =>
        {
            var body = await Body(request);
            var vars = body.GetProperty("variables");
            Assert.Equal(["d1"], vars.GetProperty("deviceIds").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("Editor", vars.GetProperty("selection")[0].GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, vars.GetProperty("selection")[0].GetProperty("version").ValueKind);
            return Json(new { data = new { result = new { totalCount = 1, items = new[] { new { device = new { id = "d1" }, events = new[] {
                new { id = "e1", occurredAt = "2026-09-20T00:00:00Z", action = "INSTALL", result = "SUCCESS", software = new { name = "Editor", version = "2", publisher = "Vendor" } }
            } } } } } });
        });
        var result = await Client(handler).DetailsAsync(new("softwareinstall", "Editor"), ["d1"], default);
        var e = Assert.Single(Assert.Single(result).Events);
        Assert.Equal("Editor", e.ItemKey);
        Assert.Equal("softwareinstall", e.Source);
        Assert.Equal("SUCCESS", e.Status);
        Assert.Contains("2", e.Title);
        Assert.Null(e.Severity);
    }

    [Fact]
    public async Task Missing_page_device_in_details_is_an_error_instead_of_partial_results()
    {
        using var handler = new Handler(_ => Task.FromResult(Json(new { data = new { result = new { totalCount = 0, items = Array.Empty<object>() } } })));
        await Assert.ThrowsAsync<GraphQLException>(() => Client(handler).DetailsAsync(new("patch", "p"), ["d1"], default));
    }

    [Theory]
    [InlineData("patch", "patches", "patch.test", "[{\"id\":\"p\",\"kbId\":\"KB1\",\"title\":\"Fix\"}]", "p", "KB1")]
    [InlineData("vulnerability", "cves", "vuln.test", "[{\"id\":\"CVE-1\",\"title\":\"Issue\"}]", "CVE-1", "CVE-1")]
    public async Task Catalog_routes_and_maps_each_id_provider(string category, string root, string host, string catalog, string key, string label)
    {
        using var handler = new Handler(async request =>
        {
            var body = await Body(request);
            Assert.Equal(host, request.RequestUri!.Host);
            Assert.Contains("result: " + root + "(", body.GetProperty("query").GetString());
            Assert.Equal("term", body.GetProperty("variables").GetProperty("search").GetString());
            Assert.Equal(2, body.GetProperty("variables").GetProperty("first").GetInt32());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"result\":" + catalog + "}}") };
        });
        var item = Assert.Single(await Client(handler).CatalogAsync(category, "term", 2, default));
        Assert.Equal(key, item.Key);
        Assert.Equal(label, item.Label);
        Assert.False(string.IsNullOrWhiteSpace(item.Detail));
    }

    [Fact]
    public async Task Software_catalog_deduplicates_any_version_and_returns_at_most_first_options()
    {
        using var handler = new Handler(async request =>
        {
            var body = await Body(request);
            Assert.Contains("result: software(", body.GetProperty("query").GetString());
            return Json(new { data = new { result = new[] {
                new { name = "Editor", version = "1", publisher = "Vendor" },
                new { name = "Editor", version = "2", publisher = "Vendor" },
                new { name = "Other", version = "1", publisher = "Vendor" },
            } } });
        });
        var options = await Client(handler).CatalogAsync("softwareinstall", null, 3, default);
        Assert.Equal(["Editor", "Editor|1", "Editor|2"], options.Select(o => o.Key));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"p\",\"kbId\":\"KB1\"}]")]
    [InlineData("[{\"id\":\"p\",\"kbId\":\"KB1\",\"title\":\"Fix\"},{\"id\":\"q\",\"kbId\":\"KB2\",\"title\":\"Fix\"}]")]
    public async Task Malformed_or_oversized_catalog_fails_atomically(string catalog)
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"result\":" + catalog + "}}") }));
        var error = await Assert.ThrowsAsync<GraphQLException>(() => Client(handler).CatalogAsync("patch", null, 1, default));
        Assert.Equal("SEARCH_SOURCE_INVALID", Assert.Single(error.Errors).Code);
    }

    [Theory]
    [InlineData(null, "p")]
    [InlineData("not-a-date", "p")]
    [InlineData("2026-09-20T00:00:00Z", "unselected")]
    public async Task Event_providers_still_reject_invalid_timestamps_and_unselected_events(string? timestamp, string patchId)
    {
        using var handler = new Handler(_ => Task.FromResult(Json(new { data = new { result = new { totalCount = 1, items = new[] { new {
            device = new { id = "d1" }, events = new[] { new { id = "e1", occurredAt = timestamp, status = "APPLIED", patch = new { id = patchId, kbId = "KB1", title = "Patch", severity = "HIGH", vendor = "Vendor" } } }
        } } } } })));
        var error = await Assert.ThrowsAsync<GraphQLException>(() => Client(handler).DetailsAsync(new("patch", "p"), ["d1"], default));
        Assert.Equal("SEARCH_SOURCE_INVALID", Assert.Single(error.Errors).Code);
    }

    internal static SearchProviderRegistry Registry() => new([new PatchSearchProvider(), new VulnerabilitySearchProvider(), new SoftwareInstallSearchProvider()]);

    internal static DomainSearchClient Client(HttpMessageHandler handler, IHttpContextAccessor? accessor = null, SearchProviderRegistry? registry = null)
    {
        accessor ??= new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (string.IsNullOrEmpty(accessor.HttpContext!.Request.Headers.Authorization)) accessor.HttpContext.Request.Headers.Authorization = "Bearer test-token";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SUBGRAPH_PATCH_URL"] = "http://patch.test/graphql",
            ["SUBGRAPH_VULNERABILITY_URL"] = "http://vuln.test/graphql",
            ["SUBGRAPH_SOFTWAREINSTALL_URL"] = "http://software.test/graphql",
        }).Build();
        return new DomainSearchClient(new HttpClient(handler, false), accessor, config, registry ?? Registry());
    }
    internal static async Task<JsonElement> Body(HttpRequestMessage request) => JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
    internal static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
    internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
