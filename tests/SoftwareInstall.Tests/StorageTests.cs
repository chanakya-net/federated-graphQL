using System.Text.Json;
using System.Text.Json.Nodes;
using SoR.Shared.Auth;
using SoR.Shared.Seeding;
using SoR.SoftwareInstall.GraphQL;
using SoR.SoftwareInstall.Seeding;
using SoR.SoftwareInstall.Storage;

namespace SoR.SoftwareInstall.Tests;

/// <summary>Blob JSON, blob naming and the resolver's filter/order/cap, without any storage.</summary>
public sealed class StorageTests
{
    [Fact]
    public void Document_roundtrips_json()
    {
        var seeded = InstallSeedData.BuildDocument(DeviceCatalog.Build(42));
        // Seed timestamps are all UTC; add one with a non-zero offset to prove the offset survives.
        var offset = new DateTimeOffset(2026, 7, 14, 14, 42, 0, TimeSpan.FromHours(5.5));
        var document = seeded with
        {
            Events = [.. seeded.Events, new StoredInstallEvent("dev-00042-i999", offset, InstallAction.Uninstall, InstallResult.Failed, new Software("7-Zip", "24.08", "Igor Pavlov"))],
        };

        var json = InstallJson.Serialize(document);
        var back = InstallJson.Deserialize<DeviceInstallDocument>(json);

        Assert.Equal((document.SchemaVersion, document.TenantId, document.DeviceId), (back.SchemaVersion, back.TenantId, back.DeviceId));
        Assert.Equal(document.Events, back.Events);
        Assert.Equal(TimeSpan.FromHours(5.5), back.Events[^1].OccurredAt.Offset);   // == on DateTimeOffset ignores the offset
        Assert.Equal(json.ToString(), InstallJson.Serialize(back).ToString());

        var text = json.ToString();
        Assert.Contains("\"occurredAt\":\"2026-07-14T14:42:00+05:30\"", text, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"UNINSTALL\"", text, StringComparison.Ordinal);
        Assert.Contains("\"result\":\"FAILED\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_shape_is_the_documented_one()
    {
        var node = JsonNode.Parse(InstallJson.Serialize(InstallSeedData.BuildDocument(DeviceCatalog.Build(42))).ToString())!.AsObject();

        Assert.Equal(["schemaVersion", "tenantId", "deviceId", "events"], node.Select(p => p.Key));
        Assert.Equal(1, (int)node["schemaVersion"]!);
        Assert.Equal("TenantA", (string)node["tenantId"]!);
        Assert.Equal("dev-00042", (string)node["deviceId"]!);
        var first = node["events"]!.AsArray()[0]!.AsObject();
        Assert.Equal(["id", "occurredAt", "action", "result", "software"], first.Select(p => p.Key));
        Assert.Equal(["name", "version", "publisher"], first["software"]!.AsObject().Select(p => p.Key));
        Assert.Contains((string)first["action"]!, new[] { "INSTALL", "UPGRADE", "UNINSTALL" });
        Assert.Contains((string)first["result"]!, new[] { "SUCCESS", "FAILED" });
        Assert.EndsWith("+00:00", (string)first["occurredAt"]!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"tenantId":"TenantA","events":[]}""")]                                  // deviceId missing
    [InlineData("""{"schemaVersion":1,"tenantId":"TenantA","deviceId":null,"events":[]}""")]                  // null for non-null
    [InlineData("""{"schemaVersion":1,"tenantId":"TenantA","deviceId":"dev-00001","events":[{"id":"x","occurredAt":"2026-01-01T00:00:00+00:00","action":"REMOVE","result":"SUCCESS","software":{"name":"a","version":"1","publisher":"p"}}]}""")]
    [InlineData("""{"schemaVersion":1,"tenantId":"TenantA","deviceId":"dev-00001","events":[{"id":"x","occurredAt":"2026-01-01T00:00:00+00:00","action":0,"result":"SUCCESS","software":{"name":"a","version":"1","publisher":"p"}}]}""")]
    public void Corrupt_documents_fail_to_deserialize(string json)
    {
        // A corrupt blob must become a field error, never a null in a non-null GraphQL field.
        Assert.ThrowsAny<JsonException>(() => InstallJson.Deserialize<DeviceInstallDocument>(BinaryData.FromString(json)));
    }

    [Fact]
    public void Blob_name_is_tenant_then_device()
    {
        Assert.Equal("TenantA/dev-00042/installEvents.json", InstallEventsBlobStore.BlobName("TenantA", "dev-00042"));
        Assert.Equal("TenantB/dev-00042/installEvents.json", InstallEventsBlobStore.BlobName("TenantB", "dev-00042"));
        Assert.StartsWith("_", InstallEventsBlobStore.MarkerBlobName, StringComparison.Ordinal);   // never a tenant prefix
    }

    [Theory]
    [InlineData("TenantA", "../TenantB/dev-07000")]   // System.Uri would normalise this into TenantB/dev-07000/...
    [InlineData("TenantA", "dev-00001/../../TenantB/dev-07000")]
    [InlineData("TenantA", "TenantB/dev-07000")]
    [InlineData("TenantA", "dev-7000")]
    [InlineData("TenantA", "dev-12000")]
    [InlineData("TenantA", "")]
    [InlineData("", "dev-00001")]
    [InlineData("..", "dev-00001")]
    [InlineData("TenantB/..", "dev-00001")]
    [InlineData("_seed", "dev-00001")]
    public void Only_canonical_ids_map_to_a_blob(string tenantId, string deviceId)
    {
        Assert.False(InstallEventsBlobStore.TryGetBlobName(tenantId, deviceId, out _));
    }

    [Fact]
    public async Task Resolver_filters_inclusively_orders_descending_and_caps()
    {
        var start = SeedConstants.Epoch - TimeSpan.FromDays(10);
        var events = Enumerable.Range(0, 1_500)
            .Select(i => new StoredInstallEvent($"dev-00001-i{i:D4}", start + TimeSpan.FromMinutes(i), InstallAction.Install, InstallResult.Success, new Software("n", "1.0.0", "p")))
            .ToList();
        var store = new FakeStore(new DeviceInstallDocument(1, "TenantA", "dev-00001", events));
        var device = new Device("dev-00001");
        var resolver = new DeviceExtensions();

        var all = await resolver.GetInstallEvents(device, null, null, new FakeCaller("TenantA"), store, default);
        Assert.NotNull(all);
        Assert.Equal(DeviceExtensions.MaxEvents, all.Count);
        Assert.Equal("dev-00001-i1499", all[0].Id);   // newest first
        Assert.Equal(all.OrderByDescending(e => e.OccurredAt), all);
        Assert.All(all, e => Assert.Equal("dev-00001", e.DeviceId));

        var window = await resolver.GetInstallEvents(device, events[10].OccurredAt, events[20].OccurredAt, new FakeCaller("TenantA"), store, default);
        Assert.Equal(Enumerable.Range(10, 11).Reverse().Select(i => $"dev-00001-i{i:D4}"), window!.Select(e => e.Id));

        var cross = await resolver.GetInstallEvents(device, null, null, new FakeCaller("TenantB"), store, default);
        Assert.Empty(cross!);
        Assert.Equal(("TenantB", "dev-00001"), store.LastRead);   // the caller's tenant picks the prefix
    }

    private sealed class FakeStore(DeviceInstallDocument document) : IInstallEventsStore
    {
        public (string Tenant, string Device) LastRead { get; private set; }

        public Task<DeviceInstallDocument?> ReadAsync(string tenantId, string deviceId, CancellationToken ct)
        {
            LastRead = (tenantId, deviceId);
            return Task.FromResult(tenantId == document.TenantId && deviceId == document.DeviceId ? document : null);
        }

        public Task WriteAsync(DeviceInstallDocument doc, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeCaller(string tenantId) : ICallerContext
    {
        public bool IsAuthenticated => true;

        public string UserId => "test";

        public string TenantId => tenantId;

        public IReadOnlySet<string> Services => new HashSet<string> { DevAuth.Services.SoftwareInstall };

        public bool HasService(string serviceName) => Services.Contains(serviceName);
    }
}
