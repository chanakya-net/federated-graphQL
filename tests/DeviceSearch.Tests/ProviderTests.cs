using System.Text.Json;
using HotChocolate;
using SoR.DeviceSearch.GraphQL;
using SoR.DeviceSearch.Providers;
using SoR.DeviceSearch.Search;

namespace SoR.DeviceSearch.Tests;

public sealed class ProviderTests
{
    [Fact]
    public void Registry_rejects_duplicate_categories_instead_of_choosing_a_provider()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new SearchProviderRegistry([new PatchSearchProvider(), new PatchSearchProvider()]));
        Assert.Contains("patch", error.Message);
    }

    [Fact]
    public async Task Registered_adapter_routes_search_and_catalog_using_its_permission_and_nullable_summary()
    {
        var registry = new SearchProviderRegistry([new SummaryProvider()]);
        using var handler = new TransportTests.Handler(async request =>
        {
            Assert.Equal("patch.test", request.RequestUri!.Host);
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            var body = await TransportTests.Body(request);
            var query = body.GetProperty("query").GetString()!;
            if (query.Contains("testCatalog", StringComparison.Ordinal))
                return TransportTests.Json(new { data = new { result = new[] { new { key = "SUMMARY", label = "Summary", detail = "Current state" } } } });
            Assert.Contains("testMatches", query);
            Assert.Equal("SUMMARY", body.GetProperty("variables").GetProperty("selection")[0].GetString());
            var item = new Dictionary<string, object> { ["device"] = new { id = "d1" } };
            if (query.Contains("events", StringComparison.Ordinal)) item["events"] = new[] { new { id = "summary-1" } };
            return TransportTests.Json(new { data = new { result = new { totalCount = 1, items = new[] { item } } } });
        });
        var engine = new DeviceSearchEngine(TransportTests.Client(handler, registry: registry), new SearchTests.Caller(["patch"]), registry);
        var result = await engine.FindAsync([new("test-summary", " summary ")], 25, 0, default);
        var summary = Assert.Single(Assert.Single(result.Items).Events);
        Assert.Equal("test-summary", summary.Source);
        Assert.Null(summary.OccurredAt);
        var item = Assert.Single((await engine.CatalogAsync("test-summary", null, 2, default))!);
        Assert.Equal("SUMMARY", item.Key);
    }

    [Fact]
    public async Task Provider_permission_can_differ_from_category_and_is_checked_before_discovery()
    {
        var registry = new SearchProviderRegistry([new SummaryProvider()]);
        var reads = 0;
        using var handler = new TransportTests.Handler(_ => { reads++; throw new InvalidOperationException("Must not contact source"); });
        var engine = new DeviceSearchEngine(TransportTests.Client(handler, registry: registry), new SearchTests.Caller(["test-summary"]), registry);
        var error = await Assert.ThrowsAsync<GraphQLException>(() => engine.FindAsync([new("test-summary", "summary")], 25, 0, default));
        Assert.Equal("AUTH_NOT_AUTHORIZED", Assert.Single(error.Errors).Code);
        Assert.Equal(0, reads);
    }

    // Test-only adapter proves the shared engine and transport need no category switch for a future provider.
    private sealed class SummaryProvider : CatalogSearchProvider
    {
        public override string Category => "test-summary";
        public override string RequiredPermission => "patch";
        protected override string EndpointSetting => "SUBGRAPH_PATCH_URL";
        protected override string SearchRoot => "testMatches";
        protected override string SelectionArgument => "keys";
        protected override string EventFields => "";
        protected override string CatalogRoot => "testCatalog";
        protected override string CatalogFields => "key label detail";
        public override SearchCapability Capability(bool available) => new(Category, "Summary", "info", "#000000", "Search summaries", "catalog", available);
        public override string NormalizeKey(string key) => base.NormalizeKey(key).ToUpperInvariant();
        public override DeviceSearchEvent MapEvent(string key, JsonElement source) => new(source.GetProperty("id").GetString()!, Category, key, null, "Summary", "Current state", "", "ACTIVE", null);
        public override IEnumerable<SearchCatalogItem> MapCatalogItem(JsonElement source) => [new(source.GetProperty("key").GetString()!, source.GetProperty("label").GetString()!, source.GetProperty("detail").GetString()!)];
    }
}
