using System.Net;
using System.Text.Json;
using SoR.Patch.Tests.Support;

namespace SoR.Patch.Tests;

/// <summary>
/// MongoDB unreachable (nothing listens on 127.0.0.1:1; 500 ms server selection), checked against the subgraph
/// directly. No Docker needed. The stub lookup survives, only the guarded fields fail, each at its own path;
/// a caller without the service still gets AUTH_NOT_AUTHORIZED (the policy runs before the resolver).
/// </summary>
public sealed class OutageTests : IAsyncLifetime
{
    private const string Unreachable = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=500&connectTimeoutMS=500";

    private readonly PatchApp _app = new(Unreachable, "patch", Tokens.SigningKey);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Health_is_503_while_mongo_is_unreachable()
    {
        using var client = _app.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Outage_nulls_only_patchEvents_with_an_error_at_the_field()
    {
        var r = await _app.QueryAsync("{ deviceById(id: \"dev-00001\") { id patchEvents { id } } }", Tokens.Alice);

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("dev-00001", r.Data.GetProperty("deviceById").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("deviceById").GetProperty("patchEvents").ValueKind);
        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal(["deviceById", "patchEvents"], Path(error));
        Assert.NotEqual("AUTH_NOT_AUTHORIZED", Code(error));
    }

    [Fact]
    public async Task Outage_on_patches_nulls_only_that_root_field()
    {
        var r = await _app.QueryAsync("{ patches { id } deviceById(id: \"dev-00001\") { id } }", Tokens.Alice);

        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("patches").ValueKind);
        Assert.Equal("dev-00001", r.Data.GetProperty("deviceById").GetProperty("id").GetString());
        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal(["patches"], Path(error));
        Assert.NotEqual("AUTH_NOT_AUTHORIZED", Code(error));
    }

    [Fact]
    public async Task Outage_with_default_timeouts_is_a_field_error_not_a_failed_request()
    {
        // Compose passes a bare "mongodb://mongo:27017". With the driver's 30 s server selection (= Hot Chocolate's
        // execution timeout) a stopped MongoDB turned the whole response into HTTP 500 (seen in the container).
        await using var app = new PatchApp("mongodb://127.0.0.1:1", "patch", Tokens.SigningKey);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var r = await app.QueryAsync("{ deviceById(id: \"dev-00001\") { id patchEvents { id } } }", Tokens.Alice);

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}; must fail within the gateway's 5 s subgraph timeout");
        Assert.Equal("dev-00001", r.Data.GetProperty("deviceById").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("deviceById").GetProperty("patchEvents").ValueKind);
        Assert.Equal(["deviceById", "patchEvents"], Path(Assert.Single(r.Errors.EnumerateArray())));
    }

    [Fact]
    public async Task Denial_wins_over_outage()
    {
        var r = await _app.QueryAsync("{ deviceById(id: \"dev-00001\") { id patchEvents { id } } }", Tokens.Carol);

        Assert.Equal("dev-00001", r.Data.GetProperty("deviceById").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("deviceById").GetProperty("patchEvents").ValueKind);
        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal(["deviceById", "patchEvents"], Path(error));
        Assert.Equal("AUTH_NOT_AUTHORIZED", Code(error));
    }

    private static string[] Path(JsonElement error) => [.. error.GetProperty("path").EnumerateArray().Select(p => p.ToString())];

    private static string? Code(JsonElement error) =>
        error.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var code) ? code.GetString() : null;
}
