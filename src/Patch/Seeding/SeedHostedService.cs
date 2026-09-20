using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using SoR.Patch.Data;
using SoR.Shared.Seeding;

namespace SoR.Patch.Seeding;

/// <summary>
/// Seeds the patch catalog and every device's patch events once (contracts/seeding.md). Marker present → skip;
/// marker absent → drop whatever a crashed attempt left and seed again; marker written last. Program.cs never
/// touches MongoDB; this runs after startup, so <c>/health</c> answers 503 until it completes.
/// </summary>
public sealed partial class SeedHostedService(
    IMongoDatabase database,
    SeedState state,
    TimeProvider clock,
    ILogger<SeedHostedService> logger) : BackgroundService
{
    public const int MaxAttempts = 10;
    public const int BatchSize = 5_000;

    /// <summary>The query path of <c>patchEvents</c>; default name <c>tenantId_1_deviceId_1_occurredAt_-1</c>.</summary>
    public static readonly CreateIndexModel<PatchEventDocument> DeviceTimelineIndex = new(
        Builders<PatchEventDocument>.IndexKeys.Ascending(e => e.TenantId).Ascending(e => e.DeviceId).Descending(e => e.OccurredAt));

    public static readonly CreateIndexModel<PatchEventDocument> TenantIndex = new(
        Builders<PatchEventDocument>.IndexKeys.Ascending(e => e.TenantId));

    /// <summary>The reverse lookup (<c>devicesWithPatches</c>): default name <c>tenantId_1_patchId_1_deviceId_1</c>.</summary>
    public static readonly CreateIndexModel<PatchEventDocument> PatchDevicesIndex = new(
        Builders<PatchEventDocument>.IndexKeys.Ascending(e => e.TenantId).Ascending(e => e.PatchId).Ascending(e => e.DeviceId));

    private static readonly BsonDocument Ping = new("ping", 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await SeedOnceAsync(stoppingToken);
                state.MarkCompleted();
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                state.MarkFailed(ex.Message);
                LogAttemptFailed(ex, attempt, MaxAttempts);
                if (attempt == MaxAttempts) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), stoppingToken);
            }
        }

        LogGaveUp(MaxAttempts);
    }

    private async Task SeedOnceAsync(CancellationToken ct)
    {
        await database.RunCommandAsync<BsonDocument>(Ping, cancellationToken: ct);

        var events = database.GetCollection<PatchEventDocument>(PatchEventDocument.Collection);
        var patches = database.GetCollection<PatchDocument>(PatchDocument.Collection);
        var markers = database.GetCollection<SeedStateDocument>(SeedStateDocument.Collection);

        var marker = await markers.Find(m => m.Key == SeedStateDocument.PatchKey).FirstOrDefaultAsync(ct);
        if (marker is not null)
        {
            await EnsureIndexesAsync(events, ct);   // idempotent; also covers a database seeded before an index existed
            LogAlreadyPresent(marker.Count, marker.CompletedAt);
            return;
        }

        var sw = Stopwatch.StartNew();

        // No marker: whatever is there is a partial previous seed. Discard it. Dropping a collection also drops
        // its indexes, so they are (re)created after the drop, before the bulk insert.
        await database.DropCollectionAsync(PatchEventDocument.Collection, ct);
        await database.DropCollectionAsync(PatchDocument.Collection, ct);
        await EnsureIndexesAsync(events, ct);

        var catalog = PatchSeedData.BuildCatalog();
        await patches.InsertManyAsync(catalog, cancellationToken: ct);

        var unordered = new InsertManyOptions { IsOrdered = false };
        var batch = new List<PatchEventDocument>(BatchSize + PatchSeedData.MaxEventsPerDevice);
        long seeded = 0;
        var devices = 0;
        foreach (var device in DeviceCatalog.All())
        {
            batch.AddRange(PatchSeedData.BuildEventsFor(device, catalog.Count));
            devices++;
            if (batch.Count >= BatchSize || devices == SeedConstants.TotalDevices)
            {
                await events.InsertManyAsync(batch, unordered, ct);
                seeded += batch.Count;
                batch.Clear();
                LogProgress(seeded, devices);
            }
        }

        // Written last. CompletedAt is bookkeeping, the only wall-clock value this service stores.
        await markers.InsertOneAsync(new SeedStateDocument
        {
            Key = SeedStateDocument.PatchKey,
            CompletedAt = clock.GetUtcNow().UtcDateTime,
            Count = seeded,
        }, cancellationToken: ct);

        LogCompleted(seeded, devices, catalog.Count, sw.ElapsedMilliseconds);
    }

    private static Task EnsureIndexesAsync(IMongoCollection<PatchEventDocument> events, CancellationToken ct) =>
        events.Indexes.CreateManyAsync([DeviceTimelineIndex, TenantIndex, PatchDevicesIndex], ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "seed already present ({Count} events, completed {CompletedAt:O}); skipping")]
    private partial void LogAlreadyPresent(long count, DateTime completedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "seeded {Seeded} events for {Devices} devices")]
    private partial void LogProgress(long seeded, int devices);

    [LoggerMessage(Level = LogLevel.Information, Message = "seed completed: {Seeded} events for {Devices} devices, {Patches} patches in {ElapsedMs} ms")]
    private partial void LogCompleted(long seeded, int devices, int patches, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "seed attempt {Attempt}/{MaxAttempts} failed")]
    private partial void LogAttemptFailed(Exception ex, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "seeding gave up after {MaxAttempts} attempts; /health stays unhealthy")]
    private partial void LogGaveUp(int maxAttempts);
}
