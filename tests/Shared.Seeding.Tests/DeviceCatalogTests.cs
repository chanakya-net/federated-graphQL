using System.Text.Json;

namespace SoR.Shared.Seeding.Tests;

public sealed class DeviceCatalogTests
{
    [Fact]
    public void DeviceId_is_zero_padded()
    {
        Assert.Equal("dev-00000", DeviceCatalog.DeviceId(0));
        Assert.Equal("dev-11999", DeviceCatalog.DeviceId(11999));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(12000)]
    public void DeviceId_rejects_out_of_range(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceCatalog.DeviceId(index));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6999)]
    [InlineData(7000)]
    [InlineData(11999)]
    public void TryGetIndex_roundtrips(int index)
    {
        Assert.True(DeviceCatalog.TryGetIndex(DeviceCatalog.DeviceId(index), out var parsed));
        Assert.Equal(index, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev-")]
    [InlineData("dev-1")]
    [InlineData("dev-000001")]
    [InlineData("dev-12000")]
    [InlineData("dev--0001")]
    [InlineData("dev-+0001")]
    [InlineData("dev- 0001")]
    [InlineData("DEV-00001")]
    [InlineData("device-00001")]
    public void TryGetIndex_rejects_non_canonical_ids(string? deviceId)
    {
        Assert.False(DeviceCatalog.TryGetIndex(deviceId, out var index));
        Assert.Equal(-1, index);
    }

    [Fact]
    public void Tenant_split_is_7000_5000()
    {
        var all = DeviceCatalog.All().ToList();
        Assert.Equal(SeedConstants.TotalDevices, all.Count);
        Assert.Equal(7000, all.Count(d => d.TenantId == SeedConstants.TenantA));
        Assert.Equal(5000, all.Count(d => d.TenantId == SeedConstants.TenantB));
        Assert.Equal(7000, DeviceCatalog.ForTenant(SeedConstants.TenantA).Count());
        Assert.Equal(5000, DeviceCatalog.ForTenant(SeedConstants.TenantB).Count());
    }

    [Fact]
    public void Ids_are_unique_and_index_derived()
    {
        var all = DeviceCatalog.All().ToList();
        Assert.Equal(all.Count, all.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, d => Assert.Equal(DeviceCatalog.DeviceId(d.Index), d.Id));
        Assert.All(all, d => Assert.Equal(DeviceCatalog.TenantOf(d.Index), d.TenantId));
    }

    [Fact]
    public void TenantOf_boundary()
    {
        // First and last id of each tenant range (plan §9, Phase 1).
        Assert.Equal(SeedConstants.TenantA, DeviceCatalog.TenantOf(0));
        Assert.Equal(SeedConstants.TenantA, DeviceCatalog.TenantOf(6999));
        Assert.Equal(SeedConstants.TenantB, DeviceCatalog.TenantOf(7000));
        Assert.Equal(SeedConstants.TenantB, DeviceCatalog.TenantOf(11999));
        Assert.Equal(("dev-00000", SeedConstants.TenantA), (DeviceCatalog.Build(0).Id, DeviceCatalog.Build(0).TenantId));
        Assert.Equal(("dev-06999", SeedConstants.TenantA), (DeviceCatalog.Build(6999).Id, DeviceCatalog.Build(6999).TenantId));
        Assert.Equal(("dev-07000", SeedConstants.TenantB), (DeviceCatalog.Build(7000).Id, DeviceCatalog.Build(7000).TenantId));
        Assert.Equal(("dev-11999", SeedConstants.TenantB), (DeviceCatalog.Build(11999).Id, DeviceCatalog.Build(11999).TenantId));
    }

    [Fact]
    public void Build_is_deterministic()
    {
        Assert.Equal(DeviceCatalog.Build(42), DeviceCatalog.Build(42));
        Assert.Equal(HostnameHash(), HostnameHash());

        // Order independence: building one device alone gives the same result as building it inside All().
        Assert.Equal(DeviceCatalog.Build(9876), DeviceCatalog.All().Single(d => d.Index == 9876));

        static int HostnameHash() => DeviceCatalog.All()
            .Select(d => d.Hostname)
            .Aggregate(17, (h, s) => unchecked(h * 31 + DeterministicRandom.StableHash(s)));
    }

    [Fact]
    public void Build_matches_golden()
    {
        // golden-devices.json was generated once in Phase 1. Never regenerate it silently: a mismatch
        // means Bogus (or the catalog) changed, and every subgraph's seeded decoration changes with it.
        var path = Path.Combine(AppContext.BaseDirectory, "golden-devices.json");
        var golden = JsonSerializer.Deserialize<List<SeedDevice>>(File.ReadAllText(path), JsonOptions)!;

        Assert.Equal(5, golden.Count);
        Assert.Equal(golden, Enumerable.Range(0, 5).Select(DeviceCatalog.Build));
    }

    [Fact]
    public void LastSeen_is_before_epoch()
    {
        var earliest = SeedConstants.Epoch - TimeSpan.FromDays(30);
        Assert.All(DeviceCatalog.All(), d =>
        {
            Assert.True(d.LastSeenAt < SeedConstants.Epoch, d.Id);
            Assert.True(d.LastSeenAt >= earliest, d.Id);
        });
    }

    [Fact]
    public void Seed_constants_are_pinned()
    {
        // contracts/seeding.md: changing any of these reseeds every store differently.
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), SeedConstants.Epoch);
        Assert.Equal(TimeSpan.FromDays(365), SeedConstants.EventWindow);
        Assert.Equal(12_000, SeedConstants.TotalDevices);
        Assert.Equal(7_000, SeedConstants.TenantADeviceCount);
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
