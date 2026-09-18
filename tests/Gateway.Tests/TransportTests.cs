using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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

        foreach (var name in SubgraphClientNames.All)
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
