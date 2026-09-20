using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using SoR.Gateway.Transport;
using SoR.Shared.Auth;

namespace SoR.Gateway.Tests.Support;

/// <summary>
/// The real gateway <c>Program</c> in-process, configured the way compose configures the container (signing key,
/// <c>SUBGRAPH_*_URL</c>, <c>SUBGRAPH_TIMEOUT_SECONDS</c>), in the <c>Production</c> environment, loading the
/// committed <c>gateway/gateway.far</c>. Subgraphs not given point at <see cref="FakeSubgraph.Unreachable"/>.
/// The app owns the fakes it is given and disposes them with itself.
/// </summary>
public sealed class GatewayApp(IReadOnlyDictionary<string, string> settings, IReadOnlyList<FakeSubgraph> subgraphs)
    : WebApplicationFactory<Program>
{
    public const string Timeline =
        """{ device(id: "dev-00001") { id hostname patchEvents { id } vulnerabilityEvents { id } installEvents { id } } }""";

    public static GatewayApp Create(
        FakeSubgraph? deviceDirectory = null,
        FakeSubgraph? patch = null,
        FakeSubgraph? vulnerability = null,
        FakeSubgraph? softwareInstall = null,
        int timeoutSeconds = 5,
        string? archive = null,
        FakeSubgraph? deviceSearch = null)
    {
        var settings = new Dictionary<string, string>
        {
            [DevAuth.SigningKeyEnv] = Tokens.SigningKey,
            [GatewaySettings.TimeoutVariable] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [GatewaySettings.ArchiveVariable] = archive ?? Repo.Archive,
            [SubgraphClientNames.UrlVariable(SubgraphClientNames.DeviceDirectory)] = UrlOf(deviceDirectory),
            [SubgraphClientNames.UrlVariable(SubgraphClientNames.Patch)] = UrlOf(patch),
            [SubgraphClientNames.UrlVariable(SubgraphClientNames.Vulnerability)] = UrlOf(vulnerability),
            [SubgraphClientNames.UrlVariable(SubgraphClientNames.SoftwareInstall)] = UrlOf(softwareInstall),
        };
        settings[SubgraphClientNames.UrlVariable(SubgraphClientNames.DeviceSearch)] = UrlOf(deviceSearch);
        FakeSubgraph?[] given = [deviceDirectory, patch, vulnerability, softwareInstall, deviceSearch];
        return new GatewayApp(settings, [.. given.OfType<FakeSubgraph>()]);
    }

    /// <summary>Every fake answering "no events": the whole timeline resolves. Order: Device Directory, Patch, Vulnerability, SoftwareInstall.</summary>
    public static async Task<GatewayApp> WithHealthySubgraphsAsync() => Create(
        await FakeSubgraph.DeviceDirectoryAsync(),
        await FakeSubgraph.NoEventsAsync("patchEvents"),
        await FakeSubgraph.NoEventsAsync("vulnerabilityEvents"),
        await FakeSubgraph.NoEventsAsync("installEvents"));

    public IReadOnlyList<FakeSubgraph> Subgraphs => subgraphs;

    public async Task<GraphQLResponse> QueryAsync(string query, string? token)
    {
        using var client = CreateClient();
        return await GraphQLResponse.PostAsync(client, "/graphql", query, token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");   // as in the container: HC 16 default security is on
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(l => l.ClearProviders());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var subgraph in subgraphs) await subgraph.DisposeAsync();
    }

    private static string UrlOf(FakeSubgraph? subgraph) => (subgraph?.Url ?? FakeSubgraph.Unreachable).ToString();
}
