using System.Net;
using System.Text.Json;
using SoR.Gateway.Tests.Support;

namespace SoR.Gateway.Tests;

/// <summary>
/// The phase-4 §9 Docker scenarios against the real compose stack (real subgraphs, stores and gateway image):
/// full federation, a stopped and a paused subgraph, a denial, tenant isolation. The same shapes against fake
/// subgraphs, without Docker, are in <see cref="TransportTests"/>.
/// </summary>
[Collection(ComposeStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StackTests(ComposeStack stack)
{
    private const string Timeline = """
        { device(id: "dev-00001") {
            id hostname os tenantId
            patchEvents { id occurredAt status patch { kbId severity } }
            vulnerabilityEvents { id occurredAt kind cve { id severity } }
            installEvents { id occurredAt action result software { name version } }
        } }
        """;

    [Fact]
    public async Task Full_timeline_federates_all_four_subgraphs()
    {
        var response = await stack.QueryAsync(Timeline, Tokens.Alice);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.False(response.HasErrors, response.ToString());
        var device = response.Data.GetProperty("device");
        Assert.Equal("dev-00001", device.GetProperty("id").GetString());
        Assert.Equal("TenantA", device.GetProperty("tenantId").GetString());
        // Device Directory needs an authenticated caller and every domain field needs its service claim, so data
        // from all four proves the gateway forwarded alice's Authorization header to each of them.
        foreach (var field in new[] { "patchEvents", "vulnerabilityEvents", "installEvents" })
        {
            Assert.Equal(JsonValueKind.Array, device.GetProperty(field).ValueKind);
        }
    }

    [Fact]
    public async Task Partial_failure_shape()
    {
        await stack.BreakAsync("patch", "stop");
        try
        {
            var response = await stack.QueryAsync(Timeline, Tokens.Alice);

            AssertOnlyPatchEventsDegraded(response);
            var search = await stack.QueryAsync("""
                { findDevices(filters: [
                    { category: "patch", key: "patch-0128" },
                    { category: "vulnerability", key: "CVE-2026-10166", connector: "or" }
                  ]) { totalCount items { device { id } } } }
                """, Tokens.Alice);
            Assert.Equal(JsonValueKind.Null, search.Data.GetProperty("findDevices").ValueKind);
            Assert.NotEmpty(search.ErrorsAt("findDevices"));
        }
        finally
        {
            await stack.RestoreAsync("patch", Timeline, Tokens.Alice);
        }
    }

    [Fact]
    public async Task Paused_subgraph_degrades_within_the_timeout()
    {
        await stack.BreakAsync("patch", "pause");
        try
        {
            var response = await stack.QueryAsync(Timeline, Tokens.Alice);

            AssertOnlyPatchEventsDegraded(response);
            // phase-4 DoD: a hung subgraph resolves within SUBGRAPH_TIMEOUT_SECONDS + 3 s.
            Assert.InRange(response.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(stack.SubgraphTimeoutSeconds + 3));
        }
        finally
        {
            await stack.RestoreAsync("patch", Timeline, Tokens.Alice);
        }
    }

    [Fact]
    public async Task Denial_shape_through_gateway()
    {
        var response = await stack.QueryAsync(Timeline, Tokens.Bob);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var device = response.Data.GetProperty("device");
        Assert.True(device.GetProperty("installEvents").IsNull(), response.ToString());
        Assert.Equal(JsonValueKind.Array, device.GetProperty("patchEvents").ValueKind);
        Assert.Equal(JsonValueKind.Array, device.GetProperty("vulnerabilityEvents").ValueKind);
        var error = Assert.Single(response.Errors);
        Assert.Single(response.ErrorsAt("device", "installEvents"));
        Assert.Equal("AUTH_NOT_AUTHORIZED", error.GetProperty("extensions").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Other_tenants_device_is_null_without_errors()
    {
        var response = await stack.QueryAsync(Timeline, Tokens.Dave);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.False(response.HasErrors, response.ToString());
        Assert.True(response.Data.GetProperty("device").IsNull(), response.ToString());
    }

    [Fact]
    public async Task Reverse_lookup_federates_patch_and_device_directory()
    {
        // devicesWithPatches: Patch returns Device stubs, Device Directory completes them (one batched lookup).
        // dev-00001 has events for patch-0128 (docs/demo.md), so it is on the sorted first page.
        var response = await stack.QueryAsync(
            """{ devicesWithPatches(patchIds: ["patch-0128"], first: 100) { totalCount items { device { id hostname tenantId } events { id status patch { id } } } } }""",
            Tokens.Alice);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.False(response.HasErrors, response.ToString());
        var result = response.Data.GetProperty("devicesWithPatches");
        Assert.True(result.GetProperty("totalCount").GetInt32() > 0);
        var items = result.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("device").GetProperty("id").GetString() == "dev-00001");
        Assert.All(items, i =>
        {
            var device = i.GetProperty("device");
            Assert.Equal("TenantA", device.GetProperty("tenantId").GetString());
            Assert.False(string.IsNullOrEmpty(device.GetProperty("hostname").GetString()));
            Assert.All(i.GetProperty("events").EnumerateArray(), e => Assert.Equal("patch-0128", e.GetProperty("patch").GetProperty("id").GetString()));
        });

        // Denied for a user without the service, at the root field; nothing else in the request is affected.
        var denied = await stack.QueryAsync("""{ devicesWithPatches(patchIds: ["patch-0128"]) { totalCount } devices(first: 1) { totalCount } }""", Tokens.Bob);
        Assert.True(denied.Data.GetProperty("devices").GetProperty("totalCount").GetInt32() > 0, denied.ToString());
        Assert.False(denied.Data.GetProperty("devicesWithPatches").IsNull(), denied.ToString());   // bob has patch
        var carol = await stack.QueryAsync("""{ devicesWithPatches(patchIds: ["patch-0128"]) { totalCount } }""", Tokens.Carol);
        Assert.True(carol.Data.GetProperty("devicesWithPatches").IsNull(), carol.ToString());
        Assert.Equal("AUTH_NOT_AUTHORIZED", Assert.Single(carol.ErrorsAt("devicesWithPatches")).GetProperty("extensions").GetProperty("code").GetString());
    }

    [Fact]
    public async Task FindDevices_evaluates_cross_domain_filters_before_pagination_and_enriches_the_page()
    {
        // Independent seed oracle from the domain APIs; production matching happens only inside DeviceSearch.
        var patches = await stack.QueryAsync("""
            { devicesWithPatches(patchIds: ["patch-0128"]) { matches { deviceIds } } }
            """, Tokens.Alice);
        var cves = await stack.QueryAsync("""
            { devicesWithCves(cveIds: ["CVE-2026-10166"]) { matches { deviceIds } } }
            """, Tokens.Alice);
        Assert.False(patches.HasErrors, patches.ToString());
        Assert.False(cves.HasErrors, cves.ToString());
        var p = patches.Data.GetProperty("devicesWithPatches").GetProperty("matches")[0]
            .GetProperty("deviceIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var v = cves.Data.GetProperty("devicesWithCves").GetProperty("matches")[0]
            .GetProperty("deviceIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
        foreach (var connector in new[] { "and", "or" })
        {
            var expected = (connector == "and" ? p.Intersect(v) : p.Union(v)).Order(StringComparer.Ordinal).ToArray();
            Assert.True(expected.Length > 2);
            var response = await stack.QueryAsync($$"""
                { findDevices(filters: [
                    { category: "patch", key: "patch-0128" },
                    { category: "vulnerability", key: "CVE-2026-10166", connector: "{{connector}}" }
                  ], first: 2, offset: 1) {
                    totalCount hasNextPage
                    items { device { id hostname tenantId } events { source itemKey status } }
                } }
                """, Tokens.Alice);
            Assert.False(response.HasErrors, response.ToString());
            var result = response.Data.GetProperty("findDevices");
            Assert.Equal(expected.Length, result.GetProperty("totalCount").GetInt32());
            var items = result.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(expected.Skip(1).Take(2), items.Select(i => i.GetProperty("device").GetProperty("id").GetString()));
            Assert.Equal(expected.Length > 3, result.GetProperty("hasNextPage").GetBoolean());
            Assert.All(items, item =>
            {
                Assert.Equal("TenantA", item.GetProperty("device").GetProperty("tenantId").GetString());
                Assert.False(string.IsNullOrEmpty(item.GetProperty("device").GetProperty("hostname").GetString()));
                var events = item.GetProperty("events").EnumerateArray().ToArray();
                Assert.NotEmpty(events);
                Assert.All(events, e => Assert.Contains(e.GetProperty("itemKey").GetString(), new[] { "patch-0128", "CVE-2026-10166" }));
                if (connector == "and") Assert.Equal(["patch", "vulnerability"], events.Select(e => e.GetProperty("source").GetString()).Distinct().Order());
            });
        }
    }

    [Fact]
    public async Task FindDevices_checks_permissions_and_tenant_context()
    {
        const string query = """
            { findDevices(filters: [{ category: "patch", key: "patch-0128" }]) {
                totalCount items { device { id tenantId hostname } }
            } }
            """;
        var denied = await stack.QueryAsync(query, Tokens.Carol);
        Assert.Equal(JsonValueKind.Null, denied.Data.GetProperty("findDevices").ValueKind);
        Assert.Equal("AUTH_NOT_AUTHORIZED", Assert.Single(denied.ErrorsAt("findDevices"))
            .GetProperty("extensions").GetProperty("code").GetString());
        var dave = await stack.QueryAsync(query, Tokens.Dave);
        Assert.False(dave.HasErrors, dave.ToString());
        Assert.True(dave.Data.GetProperty("findDevices").GetProperty("totalCount").GetInt32() > 0);
        Assert.All(dave.Data.GetProperty("findDevices").GetProperty("items").EnumerateArray(), item =>
            Assert.Equal("TenantB", item.GetProperty("device").GetProperty("tenantId").GetString()));
    }

    [Fact]
    public async Task Unauthenticated_request_is_401_with_empty_body()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = new StringContent("""{"query":"{ devices { totalCount } }"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await stack.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Search_provider_catalogs_and_permissions_work_through_the_gateway()
    {
        const string query = """
            { searchCapabilities { category name icon color placeholder filterKind available } }
            """;
        var alice = await stack.QueryAsync(query, Tokens.Alice);
        Assert.False(alice.HasErrors, alice.ToString());
        var capabilities = alice.Data.GetProperty("searchCapabilities").EnumerateArray().ToArray();
        Assert.Equal(["patch", "softwareinstall", "vulnerability"],
            capabilities.Select(c => c.GetProperty("category").GetString()).Order());
        Assert.All(capabilities, c =>
        {
            Assert.True(c.GetProperty("available").GetBoolean());
            Assert.Equal("catalog", c.GetProperty("filterKind").GetString());
        });
        var bob = await stack.QueryAsync(query, Tokens.Bob);
        Assert.False(bob.HasErrors, bob.ToString());
        Assert.False(bob.Data.GetProperty("searchCapabilities").EnumerateArray()
            .Single(c => c.GetProperty("category").GetString() == "softwareinstall")
            .GetProperty("available").GetBoolean());
        foreach (var category in new[] { "patch", "vulnerability", "softwareinstall" })
        {
            var catalog = await stack.QueryAsync($$"""
                { searchCatalog(category: "{{category}}", first: 5) { key label detail } }
                """, Tokens.Alice);
            Assert.False(catalog.HasErrors, catalog.ToString());
            var items = catalog.Data.GetProperty("searchCatalog").EnumerateArray().ToArray();
            Assert.InRange(items.Length, 1, 5);
            Assert.All(items, item => Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("key").GetString())));
        }
        var denied = await stack.QueryAsync("""
            { searchCatalog(category: "softwareinstall") { key } }
            """, Tokens.Bob);
        Assert.Equal(JsonValueKind.Null, denied.Data.GetProperty("searchCatalog").ValueKind);
        Assert.Equal("AUTH_NOT_AUTHORIZED", Assert.Single(denied.ErrorsAt("searchCatalog"))
            .GetProperty("extensions").GetProperty("code").GetString());
        var unknown = await stack.QueryAsync("""
            { searchCatalog(category: "unknown") { key } }
            """, Tokens.Alice);
        Assert.Equal("BAD_USER_INPUT", Assert.Single(unknown.ErrorsAt("searchCatalog"))
            .GetProperty("extensions").GetProperty("code").GetString());
    }

    /// <summary>contracts/errors.md "Outage": device intact, only patchEvents null, its error has no extensions.</summary>
    private static void AssertOnlyPatchEventsDegraded(GraphQLResponse response)
    {
        Assert.Equal(HttpStatusCode.OK, response.Status);
        var device = response.Data.GetProperty("device");
        Assert.Equal("dev-00001", device.GetProperty("id").GetString());
        Assert.True(device.GetProperty("patchEvents").IsNull(), response.ToString());
        Assert.Equal(JsonValueKind.Array, device.GetProperty("vulnerabilityEvents").ValueKind);
        Assert.Equal(JsonValueKind.Array, device.GetProperty("installEvents").ValueKind);
        var error = Assert.Single(response.Errors);
        Assert.Single(response.ErrorsAt("device", "patchEvents"));
        Assert.False(error.TryGetProperty("extensions", out _), response.ToString());
    }
}
