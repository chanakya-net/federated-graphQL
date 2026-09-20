using SoR.Patch.Data;
using SoR.Patch.Seeding;
using SoR.Shared.Seeding;

namespace SoR.Patch.Tests.Support;

/// <summary>What the seed functions say the database must contain (computed, never hard-coded from a run).</summary>
internal static class Expected
{
    private static readonly Lazy<IReadOnlyDictionary<string, long>> PerTenant = new(() =>
        DeviceCatalog.All()
            .GroupBy(d => d.TenantId)
            .ToDictionary(g => g.Key, g => (long)g.Sum(d => PatchSeedData.BuildEventsFor(d).Count)));

    public static long TotalEvents => PerTenant.Value.Values.Sum();

    public static long EventsOfTenant(string tenantId) => PerTenant.Value[tenantId];

    public static IReadOnlyList<PatchEventDocument> EventsOf(int deviceIndex) =>
        PatchSeedData.BuildEventsFor(DeviceCatalog.Build(deviceIndex));

    /// <summary>The order <c>patchEvents</c> promises: newest first, id ascending on equal timestamps.</summary>
    public static IReadOnlyList<PatchEventDocument> NewestFirst(IEnumerable<PatchEventDocument> events) =>
        [.. events.OrderByDescending(e => e.OccurredAt).ThenBy(e => e.Id, StringComparer.Ordinal)];

    /// <summary>
    /// What <c>devicesWithPatches</c> promises for a tenant and a selection: every device of the tenant with at least
    /// one event for a selected patch, ordered by device id, each with those events newest first.
    /// </summary>
    public static IReadOnlyList<(string DeviceId, IReadOnlyList<PatchEventDocument> Events)> DevicesWith(string tenantId, IEnumerable<string> patchIds)
    {
        var selected = patchIds.ToHashSet(StringComparer.Ordinal);
        return
        [
            .. DeviceCatalog.ForTenant(tenantId)
                .Select(d => (d.Id, Events: NewestFirst(PatchSeedData.BuildEventsFor(d).Where(e => selected.Contains(e.PatchId)))))
                .Where(x => x.Events.Count > 0)
                .OrderBy(x => x.Id, StringComparer.Ordinal),
        ];
    }
}
