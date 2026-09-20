using SoR.DeviceSearch.Providers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using SoR.DeviceSearch.GraphQL;
using SoR.DeviceSearch.Transport;
using SoR.Shared.Auth;

namespace SoR.DeviceSearch.Tests;

public sealed class EndpointTests
{
    [Fact]
    public async Task Schema_contract_includes_nullable_result_and_device_reference()
    {
        var schema = await new ServiceCollection().AddGraphQLServer(DeviceSearchSchema.Name).AddDeviceSearchTypes().BuildSchemaAsync(DeviceSearchSchema.Name);
        var sdl = Utf8GraphQLParser.Parse(schema.ToString());
        ObjectTypeDefinitionNode Type(string name) => sdl.Definitions.OfType<ObjectTypeDefinitionNode>().Single(x => x.Name.Value == name);
        var find = Type("Query").Fields.Single(x => x.Name.Value == "findDevices");
        Assert.Equal("FindDevicesResult", find.Type.ToString());
        Assert.Equal(["filters: [DeviceSearchFilterInput!]!", "first: Int! = 25", "offset: Int! = 0"], find.Arguments.Select(a => a.ToString()));
        Assert.Equal(["items: [DeviceSearchItem!]!", "totalCount: Int!", "hasNextPage: Boolean!"], Type("FindDevicesResult").Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
        Assert.Equal(["device: Device!", "events: [DeviceSearchEvent!]!"], Type("DeviceSearchItem").Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
        Assert.Equal(["id: ID!"], Type("Device").Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
        Assert.Contains(Type("Device").Fields.Single().Directives, d => d.Name.Value == "shareable");
        var input = sdl.Definitions.OfType<InputObjectTypeDefinitionNode>().Single(x => x.Name.Value == "DeviceSearchFilterInput");
        Assert.Equal(["category: String!", "key: String!", "connector: String! = \"and\""], input.Fields.Select(f => f.ToString()));
        Assert.Contains(Type("Query").Directives, d => d.Name.Value == "authorize");
    }

    [Fact]
    public async Task Capabilities_report_caller_permissions_without_contacting_sources()
    {
        using var app = new SearchApp();
        using var client = app.CreateClient();
        var result = await Post(client, "{ searchCapabilities { category name icon color placeholder filterKind available } }", Token(["patch"]));
        Assert.False(result.TryGetProperty("errors", out _), result.ToString());
        var capabilities = result.GetProperty("data").GetProperty("searchCapabilities").EnumerateArray().ToArray();
        Assert.Equal(["patch", "vulnerability", "softwareinstall"], capabilities.Select(c => c.GetProperty("category").GetString()));
        Assert.Equal([true, false, false], capabilities.Select(c => c.GetProperty("available").GetBoolean()));
        Assert.All(capabilities, c => Assert.Equal("catalog", c.GetProperty("filterKind").GetString()));
        Assert.Equal(0, app.Domain.Reads);
    }

    [Fact]
    public async Task Catalog_denial_and_unknown_category_fail_before_network()
    {
        using var app = new SearchApp();
        using var client = app.CreateClient();
        foreach (var (category, code) in new[] { ("vulnerability", "AUTH_NOT_AUTHORIZED"), ("unknown", "BAD_USER_INPUT") })
        {
            var result = await Post(client, "{ searchCatalog(category: \"" + category + "\") { key label detail } }", Token(["patch"]));
            Assert.Equal(JsonValueKind.Null, result.GetProperty("data").GetProperty("searchCatalog").ValueKind);
            Assert.Contains(code, result.ToString());
        }
        Assert.Equal(0, app.Domain.Reads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Catalog_invalid_bounds_fail_before_network(int first)
    {
        using var app = new SearchApp();
        using var client = app.CreateClient();
        var result = await Post(client, "{ searchCatalog(category: \"patch\", first: " + first + ") { key } }", Token(["patch"]));
        Assert.Contains("BAD_USER_INPUT", result.ToString());
        Assert.Equal(0, app.Domain.Reads);
    }

    [Fact]
    public async Task Catalog_endpoint_routes_success_and_preserves_caller_token()
    {
        var token = Token(["patch"]);
        using var handler = new TransportTests.Handler(async request =>
        {
            Assert.Equal(token, request.Headers.Authorization!.Parameter);
            var body = await TransportTests.Body(request);
            Assert.Contains("result: patches(", body.GetProperty("query").GetString());
            Assert.Equal("term", body.GetProperty("variables").GetProperty("search").GetString());
            return TransportTests.Json(new { data = new { result = new[] { new { id = "p", kbId = "KB1", title = "Patch" } } } });
        });
        using var app = new SearchApp(handler);
        using var client = app.CreateClient();
        var result = await Post(client, "{ searchCatalog(category: \"patch\", search: \" term \", first: 2) { key label detail } }", token);
        Assert.False(result.TryGetProperty("errors", out _), result.ToString());
        var item = Assert.Single(result.GetProperty("data").GetProperty("searchCatalog").EnumerateArray());
        Assert.Equal("p", item.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Real_endpoint_authenticates_and_denies_required_service_before_network()
    {
        using var app = new SearchApp();
        using var client = app.CreateClient();
        const string query = "{ findDevices(filters: [{ category: \"patch\", key: \"p\" }]) { totalCount items { device { id } } } }";
        var anonymous = await Post(client, query, null);
        Assert.Contains("AUTH_NOT_AUTHENTICATED", anonymous.ToString());
        var denied = await Post(client, query, Token(["softwareinstall"]));
        Assert.Equal(JsonValueKind.Null, denied.GetProperty("data").GetProperty("findDevices").ValueKind);
        Assert.Contains("AUTH_NOT_AUTHORIZED", denied.ToString());
        Assert.Equal(0, app.Domain.Reads);
        var permitted = await Post(client, query, Token(["patch"]));
        Assert.False(permitted.TryGetProperty("errors", out _), permitted.ToString());
        Assert.Equal(0, permitted.GetProperty("data").GetProperty("findDevices").GetProperty("totalCount").GetInt32());
        Assert.Equal(1, app.Domain.Reads);
    }

    [Fact]
    public async Task Every_public_object_and_input_matches_the_checked_in_contract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SoR.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var expected = Utf8GraphQLParser.Parse(await File.ReadAllTextAsync(Path.Combine(directory.FullName, "contracts", "device-search.graphqls")));
        var schema = await new ServiceCollection().AddGraphQLServer(DeviceSearchSchema.Name).AddDeviceSearchTypes().BuildSchemaAsync(DeviceSearchSchema.Name);
        var actual = Utf8GraphQLParser.Parse(schema.ToString());
        foreach (var wanted in expected.Definitions.OfType<ObjectTypeDefinitionNode>())
        {
            var found = actual.Definitions.OfType<ObjectTypeDefinitionNode>().Single(t => t.Name.Value == wanted.Name.Value);
            Assert.Equal(wanted.Fields.Select(f => $"{f.Name.Value}: {f.Type}"), found.Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
            foreach (var field in wanted.Fields)
            {
                var foundField = found.Fields.Single(f => f.Name.Value == field.Name.Value);
                Assert.Equal(field.Arguments.Select(a => a.ToString()), foundField.Arguments.Select(a => a.ToString()));
            }
        }
        foreach (var wanted in expected.Definitions.OfType<InputObjectTypeDefinitionNode>())
        {
            var found = actual.Definitions.OfType<InputObjectTypeDefinitionNode>().Single(t => t.Name.Value == wanted.Name.Value);
            Assert.Equal(wanted.Fields.Select(f => f.ToString()), found.Fields.Select(f => f.ToString()));
        }
    }

    [Fact]
    public async Task Validated_tenant_tokens_reach_domains_and_results_remain_request_scoped()
    {
        var tokens = new List<string>();
        using var handler = new TransportTests.Handler(async request =>
        {
            var bearer = request.Headers.Authorization!.Parameter!;
            tokens.Add(bearer);
            var tenant = new JsonWebToken(bearer).Claims.Single(c => c.Type == DevAuth.TenantClaim).Value;
            var body = await TransportTests.Body(request);
            var includeEvents = body.GetProperty("query").GetString()!.Contains("events", StringComparison.Ordinal);
            var item = new Dictionary<string, object> { ["device"] = new { id = tenant + "-device" } };
            if (includeEvents) item["events"] = new[] { new { id = "e1", occurredAt = "2026-09-20T00:00:00Z", status = "APPLIED", patch = new { id = "p", kbId = "KB1", title = "Patch", severity = "HIGH", vendor = "Vendor" } } };
            return TransportTests.Json(new { data = new { result = new { totalCount = 1, items = new[] { item } } } });
        });
        using var app = new SearchApp(handler);
        using var client = app.CreateClient();
        const string query = "{ findDevices(filters: [{ category: \"patch\", key: \"p\" }]) { totalCount items { device { id } events { id source itemKey } } } }";
        foreach (var tenant in new[] { "tenant-a", "tenant-b" })
        {
            var token = DevTokenFactory.Create(SigningKey, "user", "Test User", tenant, ["patch"], DateTimeOffset.UtcNow.AddDays(1));
            var result = await Post(client, query, token);
            Assert.False(result.TryGetProperty("errors", out _), result.ToString());
            Assert.Equal(tenant + "-device", result.GetProperty("data").GetProperty("findDevices").GetProperty("items")[0].GetProperty("device").GetProperty("id").GetString());
            Assert.Equal([token, token], tokens.TakeLast(2));
        }
        Assert.Equal(4, tokens.Count);
    }

    private const string SigningKey = "device-search-tests-signing-key-0123456789abcdef0123456789abcdef";
    private static string Token(string[] services) => DevTokenFactory.Create(SigningKey, "user", "Test User", "tenant-a", services, DateTimeOffset.UtcNow.AddDays(1));
    private static async Task<JsonElement> Post(HttpClient client, string query, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query }) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
    private sealed class SearchApp(HttpMessageHandler? handler = null) : WebApplicationFactory<Program>
    {
        public EmptyDomain Domain { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(DevAuth.SigningKeyEnv, SigningKey);
            builder.UseSetting("SUBGRAPH_PATCH_URL", "http://patch.test/graphql");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDomainSearchClient>();
                if (handler is null) services.AddSingleton<IDomainSearchClient>(Domain);
                else services.AddScoped<IDomainSearchClient>(sp => new DomainSearchClient(new HttpClient(handler, false), sp.GetRequiredService<IHttpContextAccessor>(), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<SearchProviderRegistry>()));
            });
        }
    }
    private sealed class EmptyDomain : IDomainSearchClient
    {
        public Task<IReadOnlyList<SearchCatalogItem>> CatalogAsync(string category, string? search, int first, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult<IReadOnlyList<SearchCatalogItem>>([]);
        }
        public int Reads;
        public Task<IReadOnlySet<string>> DiscoverAsync(DeviceSearchFilterInput filter, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        }
        public Task<IReadOnlyList<DeviceSearchItem>> DetailsAsync(DeviceSearchFilterInput filter, IReadOnlyList<string> ids, CancellationToken ct) => throw new InvalidOperationException("Empty search must not fetch details");
    }
}
