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

public sealed record SubgraphRequest(string? Authorization, string Body, IReadOnlyDictionary<string, string> Headers);

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

    public static Task<FakeSubgraph> RespondingAsync(string json) => StartAsync((_, _) => Task.FromResult((json, "application/json")));

    /// <summary>Accepts the request and never answers (a paused container), until the caller gives up.</summary>
    public static Task<FakeSubgraph> HangingAsync() => StartAsync(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return (string.Empty, "application/json");
    });

    /// <summary>Device Directory's answer to the gateway's <c>device</c> lookup.</summary>
    public static Task<FakeSubgraph> DeviceDirectoryAsync(string id = "dev-00001", string hostname = "alpha") =>
        RespondingAsync(JsonSerializer.Serialize(new { data = new { device = new { id, hostname } } }));

    /// <summary>
    /// Device Directory completing a LIST of device stubs (the reverse lookups): the gateway sends one request whose
    /// <c>variables</c> is an array (variable batching), and a Hot Chocolate server with batching allowed answers
    /// <c>application/jsonl</c>, one <c>{"variableIndex": n, "data": ...}</c> line per variable set. A single variable
    /// set gets a plain response. <paramref name="devices"/> maps id to hostname; an unknown id is <c>device: null</c>.
    /// </summary>
    public static Task<FakeSubgraph> DeviceDirectoryBatchAsync(IReadOnlyDictionary<string, string> devices) =>
        StartAsync((request, _) =>
        {
            using var body = JsonDocument.Parse(request.Body);
            var variables = body.RootElement.GetProperty("variables");
            string Answer(JsonElement set)
            {
                var id = set.EnumerateObject().Single().Value.GetString()!;
                return devices.TryGetValue(id, out var hostname)
                    ? JsonSerializer.Serialize(new { data = new { device = new { id, hostname, os = "Ubuntu 22.04" } } })
                    : """{"data":{"device":null}}""";
            }

            if (variables.ValueKind != JsonValueKind.Array) return Task.FromResult((Answer(variables), "application/graphql-response+json"));
            var lines = variables.EnumerateArray().Select((set, i) => $"{{\"variableIndex\":{i},{Answer(set)[1..]}");
            return Task.FromResult((string.Join('\n', lines) + "\n", "application/jsonl"));
        });

    /// <summary>A domain subgraph's answer to <c>deviceById { &lt;field&gt; { id } }</c>: no events.</summary>
    public static Task<FakeSubgraph> NoEventsAsync(string field) =>
        RespondingAsync(JsonSerializer.Serialize(
            new { data = new { deviceById = new Dictionary<string, object[]> { [field] = [] } } }));

    /// <summary><paramref name="respond"/> returns the body and its content type.</summary>
    public static async Task<FakeSubgraph> StartAsync(Func<SubgraphRequest, CancellationToken, Task<(string Body, string ContentType)>> respond)
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
            var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var request = new SubgraphRequest(auth.Length == 0 ? null : auth, await reader.ReadToEndAsync(ctx.RequestAborted), headers);
            fake._requests.Enqueue(request);
            var (body, contentType) = await respond(request, ctx.RequestAborted);
            return Results.Text(body, contentType);
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
