using MongoDB.Driver;
using SoR.Patch.GraphQL;
using SoR.Patch.Seeding;

namespace SoR.Patch.Data;

public interface IPatchStore
{
    /// <summary>
    /// Events of <paramref name="deviceId"/> in <paramref name="tenantId"/>, newest first, at most
    /// <see cref="PatchStore.MaxEvents"/>; <paramref name="since"/> / <paramref name="until"/> are inclusive.
    /// Empty (never an error) when nothing matches, including another tenant's device.
    /// </summary>
    Task<IReadOnlyList<PatchEvent>> GetEventsAsync(
        string tenantId, string deviceId, DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct);

    /// <summary>One page of the catalog ordered by id. Callers clamp <paramref name="first"/> and <paramref name="offset"/>.</summary>
    Task<IReadOnlyList<PatchInfo>> GetPatchesAsync(int first, int offset, CancellationToken ct);
}

/// <summary>
/// MongoDB-backed store. Events are read through the index <c>{ tenantId: 1, deviceId: 1, occurredAt: -1 }</c>;
/// the 300-entry catalog is joined from memory (loaded once the seed is complete, reloaded if an id is missing).
/// </summary>
public sealed class PatchStore(IMongoDatabase database, SeedState seed) : IPatchStore
{
    public const int MaxEvents = 1_000;

    private readonly IMongoCollection<PatchEventDocument> _events =
        database.GetCollection<PatchEventDocument>(PatchEventDocument.Collection);

    private readonly IMongoCollection<PatchDocument> _patches =
        database.GetCollection<PatchDocument>(PatchDocument.Collection);

    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private volatile IReadOnlyDictionary<string, PatchInfo>? _catalog;

    public async Task<IReadOnlyList<PatchEvent>> GetEventsAsync(
        string tenantId, string deviceId, DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct)
    {
        var f = Builders<PatchEventDocument>.Filter;
        var filter = f.Eq(e => e.TenantId, tenantId) & f.Eq(e => e.DeviceId, deviceId);
        if (since is { } s) filter &= f.Gte(e => e.OccurredAt, CeilingToMillisecond(s));
        if (until is { } u) filter &= f.Lte(e => e.OccurredAt, FloorToMillisecond(u));

        var docs = await _events.Find(filter)
            .Sort(Builders<PatchEventDocument>.Sort.Descending(e => e.OccurredAt).Ascending(e => e.Id))   // id: stable ties
            .Limit(MaxEvents)
            .ToListAsync(ct);
        if (docs.Count == 0) return [];

        var catalog = await GetCatalogAsync(reload: false, ct);
        var result = new List<PatchEvent>(docs.Count);
        foreach (var d in docs)
        {
            if (!catalog.TryGetValue(d.PatchId, out var patch))
            {
                catalog = await GetCatalogAsync(reload: true, ct);
                patch = catalog.TryGetValue(d.PatchId, out var reloaded)
                    ? reloaded
                    : throw new InvalidOperationException($"patch event {d.Id} references unknown patch {d.PatchId}");
            }

            result.Add(new PatchEvent(d.Id, d.DeviceId, AsUtc(d.OccurredAt), StoredEnum.Parse<PatchStatus>(d.Status), patch));
        }

        return result;
    }

    public async Task<IReadOnlyList<PatchInfo>> GetPatchesAsync(int first, int offset, CancellationToken ct)
    {
        var docs = await _patches.Find(FilterDefinition<PatchDocument>.Empty)
            .Sort(Builders<PatchDocument>.Sort.Ascending(p => p.Id))
            .Skip(offset)
            .Limit(first)
            .ToListAsync(ct);
        return docs.ConvertAll(ToInfo);
    }

    /// <summary>
    /// MongoDB stores milliseconds and truncates finer values, so an inclusive lower bound is rounded up and an
    /// inclusive upper bound down: <c>since</c> 12:00:00.0004 must not match an event at 12:00:00.000.
    /// </summary>
    internal static DateTime CeilingToMillisecond(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        var remainder = utc.Ticks % TimeSpan.TicksPerMillisecond;
        if (remainder == 0) return utc;
        return utc.Ticks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerMillisecond
            ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : utc.AddTicks(TimeSpan.TicksPerMillisecond - remainder);
    }

    internal static DateTime FloorToMillisecond(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
    }

    internal static PatchInfo ToInfo(PatchDocument d) =>
        new(d.Id, d.KbId, d.Title, StoredEnum.Parse<PatchSeverity>(d.Severity), d.Vendor, AsUtc(d.ReleasedAt));

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private async Task<IReadOnlyDictionary<string, PatchInfo>> GetCatalogAsync(bool reload, CancellationToken ct)
    {
        if (!reload && _catalog is { } cached) return cached;

        await _catalogGate.WaitAsync(ct);
        try
        {
            if (!reload && _catalog is { } loadedMeanwhile) return loadedMeanwhile;

            var docs = await _patches.Find(FilterDefinition<PatchDocument>.Empty).ToListAsync(ct);
            var catalog = docs.ToDictionary(d => d.Id, ToInfo, StringComparer.Ordinal);

            // Cache only a complete catalog: before the marker exists the collection may be empty or being rewritten.
            if (seed.Completed && catalog.Count > 0) _catalog = catalog;
            return catalog;
        }
        finally
        {
            _catalogGate.Release();
        }
    }
}
