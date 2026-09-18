using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SoR.DeviceDirectory.Data;
using SoR.Shared.Seeding;

namespace SoR.DeviceDirectory.Seeding;

/// <summary>
/// Migrates the owned schema and seeds <see cref="DeviceCatalog.All"/> once (contracts/seeding.md).
/// Marker present → skip; marker absent → discard any partial rows and seed again; marker written last.
/// </summary>
public sealed partial class SeedHostedService(
    IServiceScopeFactory scopes,
    SeedState state,
    ILogger<SeedHostedService> logger) : BackgroundService
{
    public const int MaxAttempts = 10;
    public const int BatchSize = 2_000;

    private const string TruncateDevicesSql = "TRUNCATE " + DeviceDbContext.Schema + ".devices";

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
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeviceDbContext>();
        // On an empty schema EF/Npgsql probes the history table first and logs one harmless
        // "Failed executing DbCommand ... __ef_migrations_history" error before creating it.
        await db.Database.MigrateAsync(ct);

        var marker = await db.SeedState.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SeedStateEntity.DevicesKey, ct);
        if (marker is not null)
        {
            LogAlreadyPresent(marker.RowCount, marker.CompletedAt);
            return;
        }

        var sw = Stopwatch.StartNew();

        // No marker: whatever is there is a partial previous seed. Discard it.
        await db.Database.ExecuteSqlRawAsync(TruncateDevicesSql, ct);

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        var seeded = 0;
        foreach (var batch in DeviceCatalog.All().Chunk(BatchSize))
        {
            db.Devices.AddRange(batch.Select(DeviceEntity.From));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            seeded += batch.Length;
            LogProgress(seeded, SeedConstants.TotalDevices);
        }

        // Written last. CompletedAt is bookkeeping, the only wall-clock value this service stores.
        db.SeedState.Add(new SeedStateEntity
        {
            Key = SeedStateEntity.DevicesKey,
            CompletedAt = DateTimeOffset.UtcNow,
            RowCount = seeded,
        });
        await db.SaveChangesAsync(ct);

        LogCompleted(seeded, sw.ElapsedMilliseconds);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "seed already present ({RowCount} devices, completed {CompletedAt:O}); skipping")]
    private partial void LogAlreadyPresent(int rowCount, DateTimeOffset completedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "seeded {Seeded}/{Total} devices")]
    private partial void LogProgress(int seeded, int total);

    [LoggerMessage(Level = LogLevel.Information, Message = "seed completed: {Seeded} devices in {ElapsedMs} ms")]
    private partial void LogCompleted(int seeded, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "seed attempt {Attempt}/{MaxAttempts} failed")]
    private partial void LogAttemptFailed(Exception ex, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "seeding gave up after {MaxAttempts} attempts; /health stays unhealthy")]
    private partial void LogGaveUp(int maxAttempts);
}
