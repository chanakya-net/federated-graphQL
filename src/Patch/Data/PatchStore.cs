using System.Text.RegularExpressions;
using MongoDB.Bson;
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

    /// <summary>
    /// One page of the catalog ordered by id; <paramref name="search"/> (optional) is a case-insensitive substring
    /// of the kbId or the title. Callers clamp <paramref name="first"/> and <paramref name="offset"/>.
    /// </summary>
    Task<IReadOnlyList<PatchInfo>> GetPatchesAsync(string? search, int first, int offset, CancellationToken ct);

    /// <summary>
    /// The devices of <paramref name="tenantId"/> with at least one event whose patch is in <paramref name="patchIds"/>,
    /// ordered by device id: one page of <paramref name="first"/> from <paramref name="offset"/> plus the unpaged count.
    /// <paramref name="deviceIds"/>, when given, restricts the candidates to those devices. Each item carries the
    /// device's events for those patches, newest first. Callers pass non-empty, distinct lists and clamp the paging.
    /// Another tenant's devices never match.
    /// </summary>
    Task<PatchDeviceMatches> FindDevicesAsync(
        string tenantId, IReadOnlyList<string> patchIds, IReadOnlyList<string>? deviceIds, int first, int offset, CancellationToken ct, bool includeEvents = true);

    /// <summary>
    /// Per patch of <paramref name="patchIds"/>, in that order, the sorted ids of the devices of <paramref name="tenantId"/>
    /// with an event for it (at most <see cref="PatchStore.MaxDeviceIdsPerMatch"/>); an unknown patch gets an empty set.
    /// </summary>
    Task<IReadOnlyList<PatchMatch>> GetMatchesAsync(string tenantId, IReadOnlyList<string> patchIds, CancellationToken ct);
}

/// <summary>
/// MongoDB-backed store. Events are read through the index <c>{ tenantId: 1, deviceId: 1, occurredAt: -1 }</c>
/// (per device) and <c>{ tenantId: 1, patchId: 1, deviceId: 1 }</c> (per patch, the reverse lookup); the 300-entry
/// catalog is joined from memory (loaded once the seed is complete, reloaded if an id is missing).
/// </summary>
public sealed class PatchStore(IMongoDatabase database, SeedState seed) : IPatchStore
{
    public const int MaxEvents = 1_000;

    /// <summary>A tenant has at most 7 000 devices (contracts/seeding.md); the cap only guards a bigger deployment.</summary>
    public const int MaxDeviceIdsPerMatch = 10_000;

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
            .Sort(NewestFirst)
            .Limit(MaxEvents)
            .ToListAsync(ct);
        return await ToEventsAsync(docs, ct);
    }

    public async Task<IReadOnlyList<PatchInfo>> GetPatchesAsync(string? search, int first, int offset, CancellationToken ct)
    {
        var filter = FilterDefinition<PatchDocument>.Empty;
        if (!string.IsNullOrWhiteSpace(search))
        {
            // The caller's text is literal: regex metacharacters in it are escaped, "i" = case-insensitive.
            var pattern = new BsonRegularExpression(Regex.Escape(search.Trim()), "i");
            var f = Builders<PatchDocument>.Filter;
            filter = f.Regex(p => p.KbId, pattern) | f.Regex(p => p.Title, pattern);
        }

        var docs = await _patches.Find(filter)
            .Sort(Builders<PatchDocument>.Sort.Ascending(p => p.Id))
            .Skip(offset)
            .Limit(first)
            .ToListAsync(ct);
        return docs.ConvertAll(ToInfo);
    }

    public async Task<PatchDeviceMatches> FindDevicesAsync(
        string tenantId, IReadOnlyList<string> patchIds, IReadOnlyList<string>? deviceIds, int first, int offset, CancellationToken ct, bool includeEvents = true)
    {
        // Distinct device ids, sorted, counted and paged in one round trip; the index
        // { tenantId, patchId, deviceId } covers the match and the group.
        var match = new BsonDocument
        {
            { "tenantId", tenantId },
            { "patchId", new BsonDocument("$in", new BsonArray(patchIds)) },
        };
        if (deviceIds is not null) match.Add("deviceId", new BsonDocument("$in", new BsonArray(deviceIds)));
        var pipeline = PipelineDefinition<PatchEventDocument, BsonDocument>.Create(
        [
            new BsonDocument("$match", match),
            new BsonDocument("$group", new BsonDocument("_id", "$deviceId")),
            new BsonDocument("$sort", new BsonDocument("_id", 1)),
            new BsonDocument("$facet", new BsonDocument
            {
                { "total", new BsonArray { new BsonDocument("$count", "n") } },
                { "page", new BsonArray { new BsonDocument("$skip", offset), new BsonDocument("$limit", first) } },
            }),
        ]);
        var facets = await _events.Aggregate(pipeline, cancellationToken: ct).SingleAsync(ct);
        var total = facets["total"].AsBsonArray.Count == 0 ? 0 : facets["total"][0]["n"].AsInt32;
        var pageIds = facets["page"].AsBsonArray.Select(d => d["_id"].AsString).ToList();
        if (pageIds.Count == 0) return new PatchDeviceMatches([], total);
        // ID-only discovery never reads full event documents or joins the catalog.
        if (!includeEvents) return new PatchDeviceMatches(pageIds.ConvertAll(id => new PatchDeviceMatch(new Device(id), [])), total);

        // The page's events for the selected patches, through the per-device index.
        var f = Builders<PatchEventDocument>.Filter;
        var docs = await _events
            .Find(f.Eq(e => e.TenantId, tenantId) & f.In(e => e.DeviceId, pageIds) & f.In(e => e.PatchId, patchIds))
            .Sort(NewestFirst)
            .ToListAsync(ct);
        var events = await ToEventsAsync(docs, ct);
        var byDevice = events.ToLookup(e => e.DeviceId, StringComparer.Ordinal);
        var items = pageIds.ConvertAll(id => new PatchDeviceMatch(new Device(id), [.. byDevice[id]]));
        return new PatchDeviceMatches(items, total);
    }

    public async Task<IReadOnlyList<PatchMatch>> GetMatchesAsync(string tenantId, IReadOnlyList<string> patchIds, CancellationToken ct)
    {
        // One group per selected patch with the set of its device ids; the same index covers it.
        var pipeline = PipelineDefinition<PatchEventDocument, BsonDocument>.Create(
        [
            new BsonDocument("$match", new BsonDocument
            {
                { "tenantId", tenantId },
                { "patchId", new BsonDocument("$in", new BsonArray(patchIds)) },
            }),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$patchId" },
                { "devices", new BsonDocument("$addToSet", "$deviceId") },
            }),
        ]);
        var groups = await _events.Aggregate(pipeline, cancellationToken: ct).ToListAsync(ct);
        var byPatch = groups.ToDictionary(
            g => g["_id"].AsString,
            g => (IReadOnlyList<string>)[.. g["devices"].AsBsonArray.Select(d => d.AsString).Order(StringComparer.Ordinal).Take(MaxDeviceIdsPerMatch)],
            StringComparer.Ordinal);
        return [.. patchIds.Select(id => new PatchMatch(id, byPatch.TryGetValue(id, out var devices) ? devices : []))];
    }

    /// <summary>The order every event list promises: newest first, id ascending on equal timestamps (stable ties).</summary>
    private static SortDefinition<PatchEventDocument> NewestFirst { get; } =
        Builders<PatchEventDocument>.Sort.Descending(e => e.OccurredAt).Ascending(e => e.Id);

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

    /// <summary>Joins the catalog entry of every document, in the documents' order.</summary>
    private async Task<IReadOnlyList<PatchEvent>> ToEventsAsync(List<PatchEventDocument> docs, CancellationToken ct)
    {
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
