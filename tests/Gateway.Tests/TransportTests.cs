using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SoR.Gateway.Tests.Support;
using SoR.Gateway.Transport;

namespace SoR.Gateway.Tests;

/// <summary>
/// Gateway to subgraph transport against in-process fake subgraphs (<see cref="FakeSubgraph"/>): header forwarding,
/// URLs from the environment, per-subgraph timeouts and the degraded response shapes of contracts/errors.md.
/// The same scenarios against the real compose stack are in <see cref="StackTests"/>.
/// </summary>
public sealed class TransportTests
{
    [Fact]
    public async Task Fourth_configured_client_uses_its_url_timeout_and_forwarded_authorization()
    {
        await using var audit = await FakeSubgraph.RespondingAsync("{}");
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer fourth-source-token";
        var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context })
            .AddTransient<ForwardAuthorizationHandler>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SubgraphClientRegistration.UrlVariable("Audit")] = audit.Url.ToString(),
            [SubgraphClientRegistration.TimeoutVariable("Audit")] = "9",
        }).Build();

        var registration = Assert.Single(SubgraphClientRegistration.Add(services, config, ["Audit"], TimeSpan.FromSeconds(5)));
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Audit");
        using var response = await client.PostAsync("", new StringContent("{}"));

        Assert.Equal(audit.Url, registration.Url);
        Assert.Equal(TimeSpan.FromSeconds(9), client.Timeout);
        Assert.Equal("Bearer fourth-source-token", Assert.Single(audit.Requests).Authorization);
    }

    [Fact]
    public void Far_source_without_a_configured_url_is_rejected()
    {
        var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor())
            .AddTransient<ForwardAuthorizationHandler>();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            SubgraphClientRegistration.Add(services, config, ["Compliance"], TimeSpan.FromSeconds(5)));

        Assert.Contains("SUBGRAPH_COMPLIANCE_URL", error.Message, StringComparison.Ordinal);
        Assert.Contains("FAR", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_is_forwarded_unchanged_to_every_subgraph()
    {
        await using var app = await GatewayApp.WithHealthySubgraphsAsync();

        var response = await app.QueryAsync(GatewayApp.Timeline, Tokens.Alice);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.False(response.HasErrors, response.ToString());
        foreach (var subgraph in app.Subgraphs)
        {
            // Each fake is reachable only through its SUBGRAPH_<NAME>_URL (the archive holds compose DNS names),
            // and only a per-subgraph client (not the "fusion" default) carries the header.
            var request = Assert.Single(subgraph.Requests);
            Assert.Equal($"Bearer {Tokens.Alice}", request.Authorization);
        }
    }

    [Fact]
    public async Task Subgraphs_receive_only_their_lookup()
    {
        await using var app = await GatewayApp.WithHealthySubgraphsAsync();
        var subgraphs = app.Subgraphs;

        await app.QueryAsync(GatewayApp.Timeline, Tokens.Alice);

        Assert.Contains("device(id: \\\"dev-00001\\\")", subgraphs[0].Requests.Single().Body, StringComparison.Ordinal);
        Assert.Contains("patchEvents", subgraphs[1].Requests.Single().Body, StringComparison.Ordinal);
        Assert.Contains("vulnerabilityEvents", subgraphs[2].Requests.Single().Body, StringComparison.Ordinal);
        Assert.Contains("installEvents", subgraphs[3].Requests.Single().Body, StringComparison.Ordinal);
        Assert.All(subgraphs.Skip(1), s => Assert.Contains("deviceById", s.Requests.Single().Body, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public async Task Timeout_is_configured(int seconds)
    {
        await using var app = GatewayApp.Create(timeoutSeconds: seconds);
        var factory = app.Services.GetRequiredService<IHttpClientFactory>();

        foreach (var name in GatewayApp.TestSubgraphs.All)
        {
            using var client = factory.CreateClient(name);
            Assert.Equal(TimeSpan.FromSeconds(seconds), client.Timeout);
        }
    }

    [Fact]
    public async Task Stopped_subgraph_degrades_only_its_field()
    {
        var deviceDirectory = await FakeSubgraph.DeviceDirectoryAsync();
        var vulnerability = await FakeSubgraph.NoEventsAsync("vulnerabilityEvents");
        var softwareInstall = await FakeSubgraph.NoEventsAsync("installEvents");
        await using var app = GatewayApp.Create(deviceDirectory, patch: null, vulnerability, softwareInstall);

        var response = await app.QueryAsync(GatewayApp.Timeline, Tokens.Alice);

        AssertOutageAtPatchEvents(response);
    }

    [Fact]
    public async Task Hung_subgraph_times_out_to_a_field_error()
    {
        const int timeoutSeconds = 1;
        var deviceDirectory = await FakeSubgraph.DeviceDirectoryAsync();
        var patch = await FakeSubgraph.HangingAsync();
        var vulnerability = await FakeSubgraph.NoEventsAsync("vulnerabilityEvents");
        var softwareInstall = await FakeSubgraph.NoEventsAsync("installEvents");
        await using var app = GatewayApp.Create(deviceDirectory, patch, vulnerability, softwareInstall, timeoutSeconds);

        var response = await app.QueryAsync(GatewayApp.Timeline, Tokens.Alice);

        AssertOutageAtPatchEvents(response);
        Assert.Single(patch.Requests);
        // Bounded by the subgraph timeout, not the 30 s execution timeout: SUBGRAPH_TIMEOUT_SECONDS + 3 s (phase-4 DoD).
        Assert.InRange(response.Elapsed, TimeSpan.FromSeconds(timeoutSeconds - 0.1), TimeSpan.FromSeconds(timeoutSeconds + 3));
    }

    [Fact]
    public async Task Denial_passes_through_with_its_code_at_the_public_path()
    {
        var deviceDirectory = await FakeSubgraph.DeviceDirectoryAsync();
        var patch = await FakeSubgraph.NoEventsAsync("patchEvents");
        var vulnerability = await FakeSubgraph.NoEventsAsync("vulnerabilityEvents");
        // The subgraph's own denial, as HC's authorization produces it at the internal lookup's path.
        var softwareInstall = await FakeSubgraph.RespondingAsync("""
            {"errors":[{"message":"The current user is not authorized to access this resource.","path":["deviceById","installEvents"],
              "extensions":{"code":"AUTH_NOT_AUTHORIZED"}}],
             "data":{"deviceById":{"installEvents":null}}}
            """);
        await using var app = GatewayApp.Create(deviceDirectory, patch, vulnerability, softwareInstall);

        var response = await app.QueryAsync(GatewayApp.Timeline, Tokens.Bob);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var device = response.Data.GetProperty("device");
        Assert.Equal(JsonValueKind.Null, device.GetProperty("installEvents").ValueKind);
        Assert.Equal(JsonValueKind.Array, device.GetProperty("patchEvents").ValueKind);
        Assert.Equal(JsonValueKind.Array, device.GetProperty("vulnerabilityEvents").ValueKind);
        var error = Assert.Single(response.Errors);
        Assert.Single(response.ErrorsAt("device", "installEvents"));
        Assert.Equal("AUTH_NOT_AUTHORIZED", error.GetProperty("extensions").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Device_directory_down_fails_the_device()
    {
        // The deliberate single point of failure (plan §7): no device, an error at ["device"], no extension calls.
        var patch = await FakeSubgraph.NoEventsAsync("patchEvents");
        await using var app = GatewayApp.Create(patch: patch);

        var response = await app.QueryAsync(GatewayApp.Timeline, Tokens.Alice);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(JsonValueKind.Null, response.Data.GetProperty("device").ValueKind);
        Assert.NotEmpty(response.ErrorsAt("device"));
        Assert.Empty(patch.Requests);
    }

    [Fact]
    public async Task Reverse_lookup_completes_device_stubs_through_device_directory_in_one_batch()
    {
        // devicesWithPatches comes from Patch with Device stubs (id only); hostname and os are Device Directory's, so the
        // gateway completes every stub through its `device(id)` lookup: one request, `variables` as an array (variable
        // batching, docs/version-facts.md §8), answered as jsonl. Order and mapping must survive, including a stub the
        // directory does not know (device: null -> the whole item nulls, the field survives with an error).
        var deviceDirectory = await FakeSubgraph.DeviceDirectoryBatchAsync(new Dictionary<string, string> { ["dev-00001"] = "alpha", ["dev-00002"] = "bravo" });
        var patch = await FakeSubgraph.RespondingAsync("""
            {"data":{"devicesWithPatches":{"totalCount":2,"items":[
              {"device":{"id":"dev-00001"},"events":[{"id":"dev-00001-p000","status":"APPLIED"}]},
              {"device":{"id":"dev-00002"},"events":[{"id":"dev-00002-p003","status":"FAILED"}]}]}}}
            """);
        await using var app = GatewayApp.Create(deviceDirectory, patch);

        var response = await app.QueryAsync(
            """{ devicesWithPatches(patchIds: ["patch-0001"]) { totalCount items { device { id hostname os } events { id status } } } }""",
            Tokens.Alice);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.False(response.HasErrors, response.ToString());
        var items = response.Data.GetProperty("devicesWithPatches").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(("dev-00001", "alpha", "APPLIED"), (items[0].GetProperty("device").GetProperty("id").GetString(), items[0].GetProperty("device").GetProperty("hostname").GetString(), items[0].GetProperty("events")[0].GetProperty("status").GetString()));
        Assert.Equal(("dev-00002", "bravo", "FAILED"), (items[1].GetProperty("device").GetProperty("id").GetString(), items[1].GetProperty("device").GetProperty("hostname").GetString(), items[1].GetProperty("events")[0].GetProperty("status").GetString()));

        var lookup = Assert.Single(deviceDirectory.Requests);
        Assert.Equal($"Bearer {Tokens.Alice}", lookup.Authorization);
        using var body = JsonDocument.Parse(lookup.Body);
        Assert.Contains("device(id: $", body.RootElement.GetProperty("query").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("variables").ValueKind);
        Assert.Equal(2, body.RootElement.GetProperty("variables").GetArrayLength());
        Assert.Contains("application/jsonl", lookup.Headers["Accept"], StringComparison.Ordinal);
        Assert.Single(patch.Requests);
    }

    [Fact]
    public async Task Reverse_lookup_with_device_directory_down_fails_the_field_not_the_request()
    {
        // Device Directory is the documented single point of failure: without it no stub can be completed, so the
        // whole (nullable) reverse-lookup field nulls with an error, while a sibling field from Patch still answers.
        // The gateway asks Patch for both root fields in one request.
        var patch = await FakeSubgraph.RespondingAsync("""
            {"data":{"devicesWithPatches":{"totalCount":1,"items":[{"device":{"id":"dev-00001"},"events":[]}]},"patches":[{"id":"patch-0001"}]}}
            """);
        await using var app = GatewayApp.Create(deviceDirectory: null, patch: patch);

        var response = await app.QueryAsync(
            """{ devicesWithPatches(patchIds: ["patch-0001"]) { totalCount items { device { id hostname } } } patches(first: 1) { id } }""",
            Tokens.Alice);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(JsonValueKind.Null, response.Data.GetProperty("devicesWithPatches").ValueKind);
        Assert.NotEmpty(response.ErrorsAt("devicesWithPatches"));
        Assert.Equal("patch-0001", response.Data.GetProperty("patches")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task FindDevices_preserves_server_page_and_enriches_only_returned_devices()
    {
        var directory = await FakeSubgraph.DeviceDirectoryBatchAsync(new Dictionary<string, string>
        {
            ["dev-00002"] = "bravo", ["dev-00003"] = "charlie",
        });
        var search = await FakeSubgraph.RespondingAsync("""
            {"data":{"findDevices":{"totalCount":320,"hasNextPage":true,"items":[
              {"device":{"id":"dev-00002"},"events":[{"id":"p2","source":"patch","label":"KB2"}]},
              {"device":{"id":"dev-00003"},"events":[{"id":"v3","source":"vulnerability","label":"CVE3"}]}]}}}
            """);
        await using var app = GatewayApp.Create(deviceDirectory: directory, deviceSearch: search);
        var response = await app.QueryAsync("""
            { findDevices(filters: [
                { category: "patch", key: "p", connector: "and" },
                { category: "vulnerability", key: "v", connector: "and" }
              ], first: 2, offset: 10) {
                totalCount hasNextPage items { device { id hostname os } events { id source label } }
            } }
            """, Tokens.Alice);
        Assert.False(response.HasErrors, response.ToString());
        var result = response.Data.GetProperty("findDevices");
        Assert.Equal(320, result.GetProperty("totalCount").GetInt32());
        Assert.True(result.GetProperty("hasNextPage").GetBoolean());
        var items = result.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("bravo", items[0].GetProperty("device").GetProperty("hostname").GetString());
        Assert.Equal("charlie", items[1].GetProperty("device").GetProperty("hostname").GetString());
        Assert.Equal("patch", items[0].GetProperty("events")[0].GetProperty("source").GetString());
        Assert.Equal($"Bearer {Tokens.Alice}", Assert.Single(search.Requests).Authorization);
        Assert.Equal($"Bearer {Tokens.Alice}", Assert.Single(directory.Requests).Authorization);
    }

    [Fact]
    public async Task FindDevices_search_failure_is_an_error_not_a_successful_empty_page()
    {
        var search = await FakeSubgraph.RespondingAsync("""
            {"data":{"findDevices":null},"errors":[{"message":"Patch is unavailable.","path":["findDevices"],
              "extensions":{"code":"SEARCH_SOURCE_UNAVAILABLE"}}]}
            """);
        await using var app = GatewayApp.Create(deviceSearch: search);
        var response = await app.QueryAsync("""
            { findDevices(filters: [{ category: "patch", key: "p" }]) { totalCount items { device { id } } } }
            """, Tokens.Alice);
        Assert.Equal(JsonValueKind.Null, response.Data.GetProperty("findDevices").ValueKind);
        Assert.Equal("SEARCH_SOURCE_UNAVAILABLE", Assert.Single(response.ErrorsAt("findDevices"))
            .GetProperty("extensions").GetProperty("code").GetString());
    }

    /// <summary>contracts/errors.md "Outage": HTTP 200, device intact, only patchEvents null, error without extensions.</summary>
    private static void AssertOutageAtPatchEvents(GraphQLResponse response)
    {
        Assert.Equal(HttpStatusCode.OK, response.Status);
        var device = response.Data.GetProperty("device");
        Assert.Equal("dev-00001", device.GetProperty("id").GetString());
        Assert.Equal("alpha", device.GetProperty("hostname").GetString());
        Assert.Equal(JsonValueKind.Null, device.GetProperty("patchEvents").ValueKind);
        Assert.Equal(JsonValueKind.Array, device.GetProperty("vulnerabilityEvents").ValueKind);
        Assert.Equal(JsonValueKind.Array, device.GetProperty("installEvents").ValueKind);

        var error = Assert.Single(response.Errors);
        Assert.Single(response.ErrorsAt("device", "patchEvents"));
        Assert.False(error.TryGetProperty("extensions", out _), response.ToString());
    }
}
