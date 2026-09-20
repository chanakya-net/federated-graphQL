using System.Diagnostics;
using System.Text.Json;

namespace SoR.Gateway.Tests.Support;

[CollectionDefinition(Name)]
public sealed class ComposeStackCollection : ICollectionFixture<ComposeStack>
{
    public const string Name = "ComposeStack";
}

/// <summary>
/// The real compose stack without the UI: the five subgraphs, their stores and the gateway, started with
/// <c>scripts/up.sh</c> (a no-op rebuild if already up) and driven with <c>scripts/demo-outage.sh</c>. Services
/// this fixture had to start are stopped again at the end; a stack that was already running is left running.
/// </summary>
public sealed class ComposeStack : IAsyncLifetime
{
    private static readonly string[] Services = ["device-directory", "patch", "vulnerability", "software-install", "device-search", "fusion-gateway"];
    private static readonly string[] Stores = ["postgres", "mongo", "azurite"];

    private IReadOnlyList<string> _runningBefore = [];

    public HttpClient Client { get; } = new()
    {
        BaseAddress = new Uri($"http://localhost:{Repo.Env("GATEWAY_PORT") ?? "5050"}"),
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>The gateway's per-subgraph timeout as compose sets it.</summary>
    public int SubgraphTimeoutSeconds { get; } = int.Parse(Repo.Env("SUBGRAPH_TIMEOUT_SECONDS") ?? "5", System.Globalization.CultureInfo.InvariantCulture);

    public async Task InitializeAsync()
    {
        var running = await RunAsync("docker", ["compose", "ps", "--services", "--status", "running"], TimeSpan.FromMinutes(1));
        _runningBefore = running.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Cold start builds five images and seeds three stores: minutes, not seconds.
        await RunAsync("scripts/up.sh", Services, TimeSpan.FromMinutes(20));
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        var startedHere = Services.Concat(Stores).Except(_runningBefore).ToArray();
        if (startedHere.Length > 0)
        {
            await RunAsync("docker", ["compose", "stop", .. startedHere], TimeSpan.FromMinutes(2));
        }
    }

    public Task<GraphQLResponse> QueryAsync(string query, string? token) => GraphQLResponse.PostAsync(Client, "/graphql", query, token);

    /// <summary><c>scripts/demo-outage.sh &lt;service&gt; stop|pause</c>.</summary>
    public Task BreakAsync(string service, string action) =>
        RunAsync("scripts/demo-outage.sh", [service, action], TimeSpan.FromMinutes(1));

    /// <summary>
    /// <c>scripts/demo-outage.sh &lt;service&gt; restore</c> (waits until healthy), then waits until the gateway serves
    /// <paramref name="query"/> without errors again, so the next test does not see a connection the gateway still
    /// holds from before the outage.
    /// </summary>
    public async Task RestoreAsync(string service, string query, string token)
    {
        await RunAsync("scripts/demo-outage.sh", [service, "restore"], TimeSpan.FromMinutes(5));
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var response = await QueryAsync(query, token);
            if (response.Status == System.Net.HttpStatusCode.OK && !response.HasErrors) return;
            if (deadline.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException($"gateway still degraded 60 s after restoring {service}: {response}");
            }

            await Task.Delay(500);
        }
    }

    private static async Task<string> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(fileName.StartsWith("scripts/", StringComparison.Ordinal) ? Repo.PathOf(fileName) : fileName)
        {
            WorkingDirectory = Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {string.Join(' ', psi.ArgumentList)} did not finish within {timeout}");
        }

        var output = await stdout;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', psi.ArgumentList)} exited {process.ExitCode}:\n{output}\n{await stderr}");
        }

        return output;
    }
}

internal static class JsonElementExtensions
{
    public static bool IsNull(this JsonElement element) => element.ValueKind == JsonValueKind.Null;
}
