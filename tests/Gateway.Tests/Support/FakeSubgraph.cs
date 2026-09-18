using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SoR.Gateway.Tests.Support;

public sealed record SubgraphRequest(string? Authorization, string Body);

/// <summary>
/// A real HTTP listener on 127.0.0.1 standing in for one subgraph: records what the gateway sends and answers
/// with a canned GraphQL response. The gateway's plan sends simple lookups, e.g. <c>device(id: "dev-00001") { id hostname }</c>
/// to Device Directory and <c>deviceById(id: $__fusion_1_id) { patchEvents { id } }</c> to Patch.
/// </summary>
public sealed class FakeSubgraph : IAsyncDisposable
{
    /// <summary>Nothing listens on port 1: connections are refused at once, like a stopped container.</summary>
    public static readonly Uri Unreachable = new("http://127.0.0.1:1/graphql");

    private readonly ConcurrentQueue<SubgraphRequest> _requests = new();
    private WebApplication? _app;

    public Uri Url { get; private set; } = Unreachable;

    public IReadOnlyCollection<SubgraphRequest> Requests => _requests;

    public static Task<FakeSubgraph> RespondingAsync(string json) => StartAsync((_, _) => Task.FromResult(json));

    /// <summary>Accepts the request and never answers (a paused container), until the caller gives up.</summary>
    public static Task<FakeSubgraph> HangingAsync() => StartAsync(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return string.Empty;
    });

    /// <summary>Device Directory's answer to the gateway's <c>device</c> lookup.</summary>
    public static Task<FakeSubgraph> DeviceDirectoryAsync(string id = "dev-00001", string hostname = "alpha") =>
        RespondingAsync(JsonSerializer.Serialize(new { data = new { device = new { id, hostname } } }));

    /// <summary>A domain subgraph's answer to <c>deviceById { &lt;field&gt; { id } }</c>: no events.</summary>
    public static Task<FakeSubgraph> NoEventsAsync(string field) =>
        RespondingAsync(JsonSerializer.Serialize(
            new { data = new { deviceById = new Dictionary<string, object[]> { [field] = [] } } }));

    public static async Task<FakeSubgraph> StartAsync(Func<SubgraphRequest, CancellationToken, Task<string>> respond)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0").UseShutdownTimeout(TimeSpan.FromSeconds(1));
        builder.Logging.ClearProviders();

        var fake = new FakeSubgraph();
        var app = builder.Build();
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var auth = ctx.Request.Headers.Authorization.ToString();
            var request = new SubgraphRequest(auth.Length == 0 ? null : auth, await reader.ReadToEndAsync(ctx.RequestAborted));
            fake._requests.Enqueue(request);
            return Results.Text(await respond(request, ctx.RequestAborted), "application/json");
        });
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        fake._app = app;
        fake.Url = new Uri($"{address}/graphql");
        return fake;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
