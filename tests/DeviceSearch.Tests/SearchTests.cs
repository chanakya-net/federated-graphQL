using SoR.DeviceSearch.Providers;
using HotChocolate;
using SoR.DeviceSearch.GraphQL;
using SoR.DeviceSearch.Search;
using SoR.DeviceSearch.Transport;
using SoR.Shared.Auth;

namespace SoR.DeviceSearch.Tests;

public sealed class SearchTests
{
    [Fact]
    public async Task And_binds_tighter_than_or_and_results_are_ordinal()
    {
        var domain = new Domain();
        domain.Sets["a"] = ["z", "a"];
        domain.Sets["b"] = ["b", "c"];
        domain.Sets["c"] = ["c"];
        var result = await Engine(domain).FindAsync([F("a"), F("b", "or"), F("c")], 25, 0, default);
        Assert.Equal(["a", "c", "z"], result.Items.Select(x => x.Device.Id));
        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task Pagination_happens_after_intersection_and_details_only_fetch_selected_ids()
    {
        var domain = new Domain();
        domain.Sets["a"] = ["a", "b", "c", "d"];
        domain.Sets["b"] = ["b", "c", "d"];
        var result = await Engine(domain).FindAsync([F("a"), F("b")], 1, 1, default);
        Assert.Equal("c", Assert.Single(result.Items).Device.Id);
        Assert.Equal(3, result.TotalCount);
        Assert.True(result.HasNextPage);
        Assert.All(domain.DetailIds, ids => Assert.Equal(["c"], ids));
    }

    [Fact]
    public async Task Unknown_key_and_empty_filters_have_no_results_or_detail_reads()
    {
        var domain = new Domain();
        Assert.Empty((await Engine(domain).FindAsync([F("unknown")], 25, 0, default)).Items);
        Assert.Empty((await Engine(domain).FindAsync([], 25, 0, default)).Items);
        Assert.Empty(domain.DetailIds);
    }

    [Fact]
    public async Task Denied_source_is_rejected_before_any_network_requests_even_after_an_empty_filter()
    {
        var domain = new Domain();
        var engine = new DeviceSearchEngine(domain, new Caller(["patch"]), TransportTests.Registry());
        var error = await Assert.ThrowsAsync<GraphQLException>(() => engine.FindAsync(
            [F("empty"), new("vulnerability", "CVE-1")], 25, 0, default));
        Assert.Equal("AUTH_NOT_AUTHORIZED", Assert.Single(error.Errors).Code);
        Assert.Equal(0, domain.Reads);
    }

    [Theory]
    [InlineData("PATCH", "x", "and", 25, 0)]
    [InlineData("patch", "", "and", 25, 0)]
    [InlineData("patch", "x", "xor", 25, 0)]
    [InlineData("softwareinstall", "|1", "and", 25, 0)]
    [InlineData("softwareinstall", "name|", "and", 25, 0)]
    [InlineData("softwareinstall", "name|1|2", "and", 25, 0)]
    [InlineData("patch", "x", "and", 101, 0)]
    [InlineData("patch", "x", "and", 0, 0)]
    [InlineData("patch", "x", "and", 25, -1)]
    public async Task Invalid_input_fails_before_network(string category, string key, string connector, int first, int offset)
    {
        var domain = new Domain();
        var error = await Assert.ThrowsAsync<GraphQLException>(() => Engine(domain).FindAsync([new(category, key, connector)], first, offset, default));
        Assert.Equal("BAD_USER_INPUT", Assert.Single(error.Errors).Code);
        Assert.Equal(0, domain.Reads);
    }

    [Fact]
    public async Task More_than_twenty_filters_is_explicit_error()
    {
        await Assert.ThrowsAsync<GraphQLException>(() => Engine(new()).FindAsync(Enumerable.Repeat(F("x"), 21).ToArray(), 25, 0, default));
    }

    [Fact]
    public async Task A_failed_required_source_fails_the_entire_search()
    {
        var domain = new Domain { FailKey = "down" };
        domain.Sets["a"] = ["a"];
        await Assert.ThrowsAsync<GraphQLException>(() => Engine(domain).FindAsync([F("a"), F("down", "or")], 25, 0, default));
        Assert.Empty(domain.DetailIds);
    }

    [Fact]
    public async Task Overlapping_software_filters_emit_each_source_event_once()
    {
        var domain = new Domain { IncludeEvents = true };
        domain.Sets["Editor"] = ["d1"];
        domain.Sets["Editor|2"] = ["d1"];
        var result = await Engine(domain).FindAsync([new("softwareinstall", "Editor"), new("softwareinstall", "Editor|2")], 25, 0, default);
        var e = Assert.Single(Assert.Single(result.Items).Events);
        Assert.Equal("Editor", e.ItemKey);
    }

    [Fact]
    public async Task Provider_normalization_deduplicates_equivalent_software_filters()
    {
        var domain = new Domain();
        domain.Sets["Editor|2"] = ["d1"];
        var result = await Engine(domain).FindAsync([new("softwareinstall", " Editor | 2 "), new("softwareinstall", "Editor|2")], 25, 0, default);
        Assert.Equal("d1", Assert.Single(result.Items).Device.Id);
        Assert.Equal(1, domain.Reads);
    }

    [Fact]
    public async Task Aggregate_discovery_limit_is_explicit_even_if_intersection_would_be_small()
    {
        var domain = new Domain();
        domain.Sets["a"] = Enumerable.Range(0, 50_000).Select(i => $"a{i}").ToArray();
        domain.Sets["b"] = Enumerable.Range(0, 50_000).Select(i => $"b{i}").ToArray();
        domain.Sets["c"] = ["c"];
        var error = await Assert.ThrowsAsync<GraphQLException>(() => Engine(domain).FindAsync([F("a"), F("b"), F("c")], 25, 0, default));
        Assert.Equal("SEARCH_LIMIT_EXCEEDED", Assert.Single(error.Errors).Code);
        Assert.Empty(domain.DetailIds);
    }

    private static DeviceSearchFilterInput F(string key, string connector = "and") => new("patch", key, connector);
    private static DeviceSearchEngine Engine(Domain domain) => new(domain, new Caller(DevAuth.Services.All), TransportTests.Registry());

    private sealed class Domain : IDomainSearchClient
    {
        public Dictionary<string, string[]> Sets { get; } = [];
        public System.Collections.Concurrent.ConcurrentBag<string[]> DetailIds { get; } = [];
        public Task<IReadOnlyList<SearchCatalogItem>> CatalogAsync(string category, string? search, int first, CancellationToken ct) => Task.FromResult<IReadOnlyList<SearchCatalogItem>>([]);
        public int Reads;
        public string? FailKey;
        public bool IncludeEvents;
        public Task<IReadOnlySet<string>> DiscoverAsync(DeviceSearchFilterInput filter, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            if (filter.Key == FailKey) throw new GraphQLException("Source down");
            return Task.FromResult<IReadOnlySet<string>>(Sets.GetValueOrDefault(filter.Key, []).ToHashSet(StringComparer.Ordinal));
        }
        public Task<IReadOnlyList<DeviceSearchItem>> DetailsAsync(DeviceSearchFilterInput filter, IReadOnlyList<string> ids, CancellationToken ct)
        {
            DetailIds.Add(ids.ToArray());
            return Task.FromResult<IReadOnlyList<DeviceSearchItem>>(ids.Select(id => new DeviceSearchItem(new Device(id), IncludeEvents ? [new DeviceSearchEvent("e1", filter.Category, filter.Key, DateTimeOffset.UnixEpoch, "Editor", "Editor 2", "Vendor", "SUCCESS", null)] : [])).ToArray());
        }
    }

    internal sealed class Caller(string[] services) : ICallerContext
    {
        public bool IsAuthenticated => true;
        public string UserId => "user";
        public string TenantId => "tenant-a";
        public IReadOnlySet<string> Services => services.ToHashSet(StringComparer.Ordinal);
        public bool HasService(string name) => Services.Contains(name);
    }
}
