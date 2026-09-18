using SoR.Patch.Data;
using SoR.Patch.GraphQL;
using SoR.Shared.Seeding;

namespace SoR.Patch.Seeding;

/// <summary>
/// Pure, deterministic seed data (contracts/seeding.md). Everything derives from <see cref="DeterministicRandom"/>
/// and <see cref="SeedConstants.Epoch"/>: no wall clock, no random GUIDs, no unseeded RNG (test
/// <c>Seed_code_never_reads_the_wall_clock_or_unseeded_randomness</c> scans this folder).
/// Draw order is part of the output; do not reorder the <c>rng</c> calls.
/// </summary>
public static class PatchSeedData
{
    public const int CatalogSize = 300;
    public const string CatalogDomain = "patch-catalog";
    public const string EventDomain = "patch";
    public const int MinEventsPerDevice = 5;
    public const int MaxEventsPerDevice = 30;
    public const int KbBase = 5_000_000;
    public const int MaxReleaseAgeDays = 730;

    public static readonly IReadOnlyList<string> Vendors = ["Microsoft", "Canonical", "Red Hat", "Apple", "Adobe", "Oracle"];

    public static string PatchId(int index) => $"patch-{index:D4}";

    public static string EventId(string deviceId, int n) => $"{deviceId}-p{n:D3}";

    /// <summary>The 300-entry catalog, <c>patch-0000</c> … <c>patch-0299</c>.</summary>
    public static IReadOnlyList<PatchDocument> BuildCatalog()
    {
        var rng = DeterministicRandom.For(0, CatalogDomain);
        var catalog = new List<PatchDocument>(CatalogSize);
        for (var i = 0; i < CatalogSize; i++)
        {
            var kbId = $"KB{KbBase + i}";
            var vendor = Vendors[rng.Next(0, Vendors.Count)];
            var severity = Severity(rng.Next(0, 100));
            var releasedAt = SeedConstants.Epoch - TimeSpan.FromDays(rng.Next(0, MaxReleaseAgeDays));
            catalog.Add(new PatchDocument
            {
                Id = PatchId(i),
                KbId = kbId,
                Title = $"{vendor} security update {kbId}",
                Severity = StoredEnum.ToStored(severity),
                Vendor = vendor,
                ReleasedAt = releasedAt.UtcDateTime,
            });
        }

        return catalog;
    }

    /// <summary>5–30 events for one device, ids <c>{deviceId}-p000</c> …, timestamps inside the event window.</summary>
    public static IReadOnlyList<PatchEventDocument> BuildEventsFor(SeedDevice device, int catalogCount = CatalogSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(catalogCount, 1);
        var rng = DeterministicRandom.For(device.Index, EventDomain);
        var count = rng.Next(MinEventsPerDevice, MaxEventsPerDevice + 1);
        var events = new List<PatchEventDocument>(count);
        for (var n = 0; n < count; n++)
        {
            var patchIndex = rng.Next(0, catalogCount);
            var occurredAt = DeterministicRandom.InstantInWindow(rng);
            var status = Status(rng.Next(0, 100));
            events.Add(new PatchEventDocument
            {
                Id = EventId(device.Id, n),
                TenantId = device.TenantId,
                DeviceId = device.Id,
                PatchId = PatchId(patchIndex),
                Status = StoredEnum.ToStored(status),
                OccurredAt = occurredAt.UtcDateTime,
            });
        }

        return events;
    }

    /// <summary>CRITICAL 15 %, HIGH 35 %, MEDIUM 35 %, LOW 15 % for a roll in 0..99.</summary>
    internal static PatchSeverity Severity(int roll) => roll switch
    {
        < 15 => PatchSeverity.Critical,
        < 50 => PatchSeverity.High,
        < 85 => PatchSeverity.Medium,
        _ => PatchSeverity.Low,
    };

    /// <summary>APPLIED 80 %, FAILED 12 %, PENDING 8 % for a roll in 0..99.</summary>
    internal static PatchStatus Status(int roll) => roll switch
    {
        < 80 => PatchStatus.Applied,
        < 92 => PatchStatus.Failed,
        _ => PatchStatus.Pending,
    };
}
