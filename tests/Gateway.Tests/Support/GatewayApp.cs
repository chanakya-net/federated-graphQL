using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using SoR.Gateway.Transport;
using SoR.Shared.Auth;
using System.Security.Cryptography;
using System.Text.Json;

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
        FakeSubgraph? deviceSearch = null,
        string? catalog = null,
        IReadOnlyDictionary<string, string>? additionalSettings = null,
        IReadOnlyList<FakeSubgraph>? additionalSubgraphs = null)
    {
        archive ??= Repo.Archive;
        catalog ??= MatchedEmptyCatalog(archive);
        var settings = new Dictionary<string, string>
        {
            [DevAuth.SigningKeyEnv] = Tokens.SigningKey,
            [GatewaySettings.TimeoutVariable] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [GatewaySettings.ArchiveVariable] = archive,
            [GatewaySettings.CatalogVariable] = catalog,
            [SubgraphClientRegistration.UrlVariable(TestSubgraphs.DeviceDirectory)] = UrlOf(deviceDirectory),
            [SubgraphClientRegistration.UrlVariable(TestSubgraphs.Patch)] = UrlOf(patch),
            [SubgraphClientRegistration.UrlVariable(TestSubgraphs.Vulnerability)] = UrlOf(vulnerability),
            [SubgraphClientRegistration.UrlVariable(TestSubgraphs.SoftwareInstall)] = UrlOf(softwareInstall),
        };
        settings[SubgraphClientRegistration.UrlVariable(TestSubgraphs.DeviceSearch)] = UrlOf(deviceSearch);
        if (additionalSettings is not null)
            foreach (var (key, value) in additionalSettings) settings[key] = value;
        FakeSubgraph?[] given = [deviceDirectory, patch, vulnerability, softwareInstall, deviceSearch];
        return new GatewayApp(settings, [.. given.OfType<FakeSubgraph>(), .. additionalSubgraphs ?? []]);
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

    private static string MatchedEmptyCatalog(string archive)
    {
        // Bad-archive tests must reach gateway startup validation; the placeholder catalog is never loaded there.
        var hash = File.Exists(archive)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant()
            : new string('0', 64);
        var path = Path.Combine(Path.GetTempPath(), $"gateway-catalog-{hash}.json");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, schemaHash = hash, sources = Array.Empty<object>() }) + "\n");
        }
        return path;
    }

    public static class TestSubgraphs
    {
        public const string DeviceDirectory = "DeviceDirectory";
        public const string Patch = "Patch";
        public const string Vulnerability = "Vulnerability";
        public const string SoftwareInstall = "SoftwareInstall";
        public const string DeviceSearch = "DeviceSearch";

        public static readonly string[] All = [DeviceDirectory, Patch, Vulnerability, SoftwareInstall, DeviceSearch];
    }
}
