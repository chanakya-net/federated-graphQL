using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using HotChocolate.Resolvers;
using HotChocolate.Transport.Formatters;
using Microsoft.Extensions.DependencyInjection;
using SoR.Shared.Auth;
using SoR.SoftwareInstall.GraphQL;
using SoR.SoftwareInstall.Storage;

namespace SoR.SoftwareInstall.Tests;

/// <summary>Real GraphQL selection execution with storage spies; no Azurite host is needed.</summary>
public sealed class SelectionTests
{
    [Fact]
    public async Task Id_discovery_returns_the_index_page_without_reading_event_blobs()
    {
        var store = new SpyStore();
        var body = await Execute(store, """
            { devicesWithSoftware(software: [{ name: "Git" }], first: 1, offset: 1) {
                totalCount items { device { id } }
            } }
            """);

        Assert.False(body.TryGetProperty("errors", out _), body.ToString());
        var result = body.GetProperty("data").GetProperty("devicesWithSoftware");
        Assert.Equal(3, result.GetProperty("totalCount").GetInt32());
        Assert.Equal("dev-00002", result.GetProperty("items")[0].GetProperty("device").GetProperty("id").GetString());
        Assert.Empty(store.Reads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Selected_events_read_only_the_requested_candidate_page_including_aliases_and_fragments(bool aliases, bool fragments)
    {
        var store = new SpyStore();
        var itemField = aliases ? "rows: items" : "items";
        var eventField = aliases ? "history: events" : "events";
        var itemSelection = $"device {{ id }} {eventField} {{ id }}";
        var selection = $"totalCount {itemField} {{ {(fragments ? "...Details" : itemSelection)} }}";
        var body = await Execute(store, $$"""
            { devicesWithSoftware(software: [{ name: "Git" }], deviceIds: ["dev-00001", "dev-00003", "dev-07000"], first: 1, offset: 1) {
                {{selection}}
            } }
            {{(fragments ? $"fragment Details on SoftwareDeviceMatch {{ {itemSelection} }}" : "")}}
            """);

        Assert.False(body.TryGetProperty("errors", out _), body.ToString());
        Assert.Equal(("TenantA", "dev-00003"), Assert.Single(store.Reads));
        var result = body.GetProperty("data").GetProperty("devicesWithSoftware");
        Assert.Equal(2, result.GetProperty("totalCount").GetInt32());
        var item = result.GetProperty(aliases ? "rows" : "items")[0];
        Assert.Equal("dev-00003", item.GetProperty("device").GetProperty("id").GetString());
        Assert.Equal("dev-00003-i001", item.GetProperty(aliases ? "history" : "events")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("totalCount")]
    [InlineData("totalCount matches { name deviceIds }")]
    [InlineData("totalCount rows: items { ...Identity }")]
    public async Task Count_sets_and_aliased_identity_fragments_do_not_read_blobs(string selection)
    {
        var store = new SpyStore();
        var body = await Execute(store, $$"""
            { devicesWithSoftware(software: [{ name: "Git" }], first: 2) { {{selection}} } }
            {{(selection.Contains("Identity", StringComparison.Ordinal) ? "fragment Identity on SoftwareDeviceMatch { owner: device { id } }" : "")}}
            """);

        Assert.False(body.TryGetProperty("errors", out _), body.ToString());
        Assert.Equal(3, body.GetProperty("data").GetProperty("devicesWithSoftware").GetProperty("totalCount").GetInt32());
        Assert.Empty(store.Reads);
    }

    private static async Task<JsonElement> Execute(SpyStore store, string query)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICallerContext>(new Caller());
        services.AddSingleton<IInstallEventsStore>(store);
        services.AddSingleton<ISoftwareIndexStore>(new IndexStore());
        services.AddGraphQLServer().AddSoftwareInstallTypes();
        services.AddSingleton<IAuthorizationHandler, AllowAuthorization>();
        await using var provider = services.BuildServiceProvider();
        var executor = await provider.GetRequestExecutorAsync();
        await using var result = await executor.ExecuteAsync(query);
        using var stream = new MemoryStream();
        var writer = PipeWriter.Create(stream);
        await JsonResultFormatter.Default.FormatAsync(Assert.IsType<OperationResult>(result), writer);
        await writer.CompleteAsync();
        using var json = JsonDocument.Parse(stream.ToArray());
        return json.RootElement.Clone();
    }

    private sealed class Caller : ICallerContext
    {
        public bool IsAuthenticated => true;
        public string UserId => "alice";
        public string TenantId => "TenantA";
        public IReadOnlySet<string> Services { get; } = new HashSet<string> { "softwareinstall" };
        public bool HasService(string serviceName) => Services.Contains(serviceName);
    }

    private sealed class IndexStore : ISoftwareIndexStore
    {
        public Task<SoftwareIndex> GetAsync(CancellationToken ct) => Task.FromResult(new SoftwareIndex(
            new SoftwareIndexDocument(1, [new SoftwareIndexEntry("Git", "2.40", "Git", new Dictionary<string, IReadOnlyList<string>> {
                ["TenantA"] = ["dev-00001", "dev-00002", "dev-00003"], ["TenantB"] = ["dev-07000"],
            })])));
    }

    private sealed class SpyStore : IInstallEventsStore
    {
        public ConcurrentQueue<(string Tenant, string Device)> Reads { get; } = new();
        public Task<DeviceInstallDocument?> ReadAsync(string tenantId, string deviceId, CancellationToken ct)
        {
            Reads.Enqueue((tenantId, deviceId));
            return Task.FromResult<DeviceInstallDocument?>(new(1, tenantId, deviceId,
                [new StoredInstallEvent($"{deviceId}-i001", DateTimeOffset.Parse("2026-09-20T00:00:00Z"), InstallAction.Install, InstallResult.Success, new Software("Git", "2.40", "Git"))]));
        }
        public Task WriteAsync(DeviceInstallDocument document, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class AllowAuthorization : IAuthorizationHandler
    {
        public ValueTask<AuthorizeResult> AuthorizeAsync(IMiddlewareContext context, AuthorizeDirective directive, CancellationToken ct = default) => ValueTask.FromResult(AuthorizeResult.Allowed);
        public ValueTask<AuthorizeResult> AuthorizeAsync(AuthorizationContext context, IReadOnlyList<AuthorizeDirective> directives, CancellationToken ct = default) => ValueTask.FromResult(AuthorizeResult.Allowed);
    }
}
