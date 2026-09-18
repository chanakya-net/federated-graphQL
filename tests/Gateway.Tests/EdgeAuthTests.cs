using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using SoR.Gateway.Tests.Support;

namespace SoR.Gateway.Tests;

/// <summary>
/// Edge authentication (contracts/http-and-env.md, contracts/errors.md): anything that could execute an operation
/// without a valid token is a <c>401</c> with an empty body and no <c>WWW-Authenticate</c>, and no subgraph is called.
/// </summary>
public sealed class EdgeAuthTests : IAsyncLifetime
{
    private GatewayApp _app = null!;

    private IReadOnlyList<FakeSubgraph> Subgraphs => _app.Subgraphs;

    public async Task InitializeAsync() => _app = await GatewayApp.WithHealthySubgraphsAsync();

    public async Task DisposeAsync() => await _app.DisposeAsync();

    public static TheoryData<string, string?> RejectedTokens => new()
    {
        { "no token", null },
        { "signed with another key", Tokens.AliceSignedWithOtherKey },
        { "expired", Tokens.AliceExpired },
        { "not a JWT", "not-a-jwt" },
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task EdgeAuth_rejects_unauthenticated_post(string because, string? token)
    {
        using var client = _app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = new StringContent($$"""{"query":{{System.Text.Json.JsonSerializer.Serialize(GatewayApp.Timeline)}}}""", System.Text.Encoding.UTF8, "application/json"),
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{because}: {(int)response.StatusCode}");
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.All(Subgraphs, s => Assert.Empty(s.Requests));
    }

    [Theory]
    [InlineData("POST", "/graphql/")]
    [InlineData("GET", "/graphql?query=%7B__typename%7D")]
    [InlineData("GET", "/graphql/?query=%7B__typename%7D")]
    public async Task EdgeAuth_rejects_other_unauthenticated_operation_requests(string method, string uri)
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        if (method == "POST") request.Content = new StringContent("""{"query":"{ __typename }"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.All(Subgraphs, s => Assert.Empty(s.Requests));
    }

    [Fact]
    public async Task EdgeAuth_allows_get_ui()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var redirect = await client.GetAsync("/graphql");
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.EndsWith("/graphql/", redirect.Headers.Location?.ToString(), StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/graphql/");
        request.Headers.Accept.ParseAdd("text/html");
        using var ui = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task EdgeAuth_passes_valid_token()
    {
        var response = await _app.QueryAsync("{ __typename }", Tokens.Alice);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("Query", response.Data.GetProperty("__typename").GetString());
    }

    [Fact]
    public async Task EdgeAuth_does_not_check_tenant_or_services()
    {
        // Bob has no softwareinstall service; the gateway lets him through and leaves the decision to the subgraphs.
        var response = await _app.QueryAsync(GatewayApp.Timeline, Tokens.Bob);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.All(Subgraphs, s => Assert.Single(s.Requests));
    }

    [Fact]
    public async Task Health_needs_no_token()
    {
        using var client = _app.CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
