using SoR.Patch.Data;
using SoR.Patch.GraphQL;
using SoR.Patch.Seeding;
using SoR.Patch.Tests.Support;
using SoR.Shared.Seeding;

namespace SoR.Patch.Tests;

/// <summary>The pure seed functions (contracts/seeding.md): deterministic, in range, ids and counts as specified.</summary>
public sealed class SeedDataTests
{
    private static readonly DateTime WindowStart = (SeedConstants.Epoch - SeedConstants.EventWindow).UtcDateTime;
    private static readonly DateTime Epoch = SeedConstants.Epoch.UtcDateTime;

    [Fact]
    public void SeedData_is_deterministic()
    {
        var first = PatchSeedData.BuildEventsFor(DeviceCatalog.Build(42));
        var second = PatchSeedData.BuildEventsFor(DeviceCatalog.Build(42));

        Assert.Equal(first.Select(Row), second.Select(Row));
        Assert.InRange(first.Count, 5, 30);
        Assert.All(first, e =>
        {
            Assert.InRange(e.OccurredAt, WindowStart, Epoch);
            Assert.Equal(DateTimeKind.Utc, e.OccurredAt.Kind);
            Assert.Equal("dev-00042", e.DeviceId);
            Assert.Equal(SeedConstants.TenantA, e.TenantId);
        });
        Assert.Equal(Enumerable.Range(0, first.Count).Select(n => $"dev-00042-p{n:D3}"), first.Select(e => e.Id));
    }

    [Fact]
    public void SeedData_catalog_has_300_and_stable_ids()
    {
        var catalog = PatchSeedData.BuildCatalog();

        Assert.Equal(300, catalog.Count);
        Assert.Equal(Enumerable.Range(0, 300).Select(i => $"patch-{i:D4}"), catalog.Select(p => p.Id));
        Assert.Equal("patch-0000", catalog[0].Id);
        Assert.Equal("patch-0299", catalog[^1].Id);
        Assert.Equal(Enumerable.Range(0, 300).Select(i => $"KB{5_000_000 + i}"), catalog.Select(p => p.KbId));
        Assert.Equal(catalog.Select(CatalogRow), PatchSeedData.BuildCatalog().Select(CatalogRow));

        Assert.All(catalog, p =>
        {
            Assert.Contains(p.Vendor, PatchSeedData.Vendors);
            Assert.Equal($"{p.Vendor} security update {p.KbId}", p.Title);
            Assert.Contains(p.Severity, (string[])["CRITICAL", "HIGH", "MEDIUM", "LOW"]);
            Assert.InRange(p.ReleasedAt, Epoch.AddDays(-729), Epoch);
            Assert.Equal(TimeSpan.Zero, p.ReleasedAt.TimeOfDay);
        });
        // 300 draws: every vendor and every severity occurs.
        Assert.Equal(PatchSeedData.Vendors.Count, catalog.Select(p => p.Vendor).Distinct().Count());
        Assert.Equal(4, catalog.Select(p => p.Severity).Distinct().Count());
    }

    [Fact]
    public void All_devices_get_5_to_30_events_referencing_the_catalog()
    {
        long total = 0;
        var statuses = new Dictionary<string, long>();
        foreach (var device in DeviceCatalog.All())
        {
            var events = PatchSeedData.BuildEventsFor(device);
            Assert.InRange(events.Count, PatchSeedData.MinEventsPerDevice, PatchSeedData.MaxEventsPerDevice);
            foreach (var e in events)
            {
                Assert.Equal(device.TenantId, e.TenantId);
                Assert.StartsWith($"{device.Id}-p", e.Id, StringComparison.Ordinal);
                Assert.True(e.OccurredAt >= WindowStart && e.OccurredAt <= Epoch, e.Id);
                Assert.True(PatchIndexOf(e.PatchId) is >= 0 and < PatchSeedData.CatalogSize, e.PatchId);
                statuses[e.Status] = statuses.GetValueOrDefault(e.Status) + 1;
            }

            total += events.Count;
        }

        // Mean of 5..30 is 17.5 -> about 210 000 (phase-3a §5), within 2 %.
        Assert.InRange(total, 205_800, 214_200);
        Assert.Equal(total, Expected.TotalEvents);
        // APPLIED 80 %, FAILED 12 %, PENDING 8 %, each within 1 percentage point.
        Assert.Equal(["APPLIED", "FAILED", "PENDING"], statuses.Keys.Order(StringComparer.Ordinal));
        Assert.InRange(statuses["APPLIED"] / (double)total, 0.79, 0.81);
        Assert.InRange(statuses["FAILED"] / (double)total, 0.11, 0.13);
        Assert.InRange(statuses["PENDING"] / (double)total, 0.07, 0.09);
    }

    [Fact]
    public void Weighted_picks_follow_the_documented_splits()
    {
        var severities = Enumerable.Range(0, 100).Select(PatchSeedData.Severity).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(15, severities[PatchSeverity.Critical]);
        Assert.Equal(35, severities[PatchSeverity.High]);
        Assert.Equal(35, severities[PatchSeverity.Medium]);
        Assert.Equal(15, severities[PatchSeverity.Low]);

        var statuses = Enumerable.Range(0, 100).Select(PatchSeedData.Status).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(80, statuses[PatchStatus.Applied]);
        Assert.Equal(12, statuses[PatchStatus.Failed]);
        Assert.Equal(8, statuses[PatchStatus.Pending]);
    }

    [Fact]
    public void Tenant_split_follows_the_device_catalog()
    {
        Assert.All(PatchSeedData.BuildEventsFor(DeviceCatalog.Build(6_999)), e => Assert.Equal(SeedConstants.TenantA, e.TenantId));
        Assert.All(PatchSeedData.BuildEventsFor(DeviceCatalog.Build(7_000)), e => Assert.Equal(SeedConstants.TenantB, e.TenantId));
        Assert.Equal(Expected.TotalEvents, Expected.EventsOfTenant(SeedConstants.TenantA) + Expected.EventsOfTenant(SeedConstants.TenantB));
    }

    [Fact]
    public void Seed_code_never_reads_the_wall_clock_or_unseeded_randomness()
    {
        // Hard rule (phases/00-execution-plan.md §8). SeedHostedService stamps completedAt through TimeProvider.
        var forbidden = new[] { "DateTime.UtcNow", "DateTime.Now", "DateTimeOffset.UtcNow", "DateTimeOffset.Now", "Guid.NewGuid", "new Random(", "Random.Shared" };
        foreach (var file in Directory.EnumerateFiles(Repo.PathOf("src", "Patch", "Seeding"), "*.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.All(forbidden, f => Assert.False(source.Contains(f, StringComparison.Ordinal), $"{Path.GetFileName(file)} uses {f}"));
        }
    }

    private static int PatchIndexOf(string patchId) =>
        patchId.StartsWith("patch-", StringComparison.Ordinal) && int.TryParse(patchId.AsSpan(6), out var i) ? i : -1;

    private static string Row(PatchEventDocument e) => $"{e.Id}|{e.TenantId}|{e.DeviceId}|{e.PatchId}|{e.Status}|{e.OccurredAt:O}";

    private static string CatalogRow(PatchDocument p) => $"{p.Id}|{p.KbId}|{p.Title}|{p.Severity}|{p.Vendor}|{p.ReleasedAt:O}";
}
