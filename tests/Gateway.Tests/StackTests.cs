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
