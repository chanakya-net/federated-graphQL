using System.Diagnostics;
using SoR.Shared.Seeding;
using SoR.SoftwareInstall.Storage;

namespace SoR.SoftwareInstall.Seeding;

/// <summary>
/// Uploads one blob per device of <see cref="DeviceCatalog.All"/> once (contracts/seeding.md). Marker
/// <c>_seed/complete.json</c> present → skip. Absent → upload everything again with overwrite (a partial earlier
/// run is replaced blob by blob, no delete pass), then write the marker last.
/// </summary>
public sealed partial class SeedHostedService(
    InstallEventsBlobStore store,
    SeedState state,
    ILogger<SeedHostedService> logger) : BackgroundService
{
    public const int MaxAttempts = 10;
    public const int MaxConcurrency = 32;
    public const int ProgressEvery = 1_000;

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
        await store.Container.CreateIfNotExistsAsync(cancellationToken: ct);

        var marker = await store.ReadMarkerAsync(ct);
        if (marker is not null)
        {
            LogAlreadyPresent(marker.DeviceCount, marker.CompletedAt);
            return;
        }

        var sw = Stopwatch.StartNew();
        var uploaded = 0;
        await Parallel.ForEachAsync(
            DeviceCatalog.All(),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct },
            async (device, token) =>
            {
                await store.WriteAsync(InstallSeedData.BuildDocument(device), token);
                var n = Interlocked.Increment(ref uploaded);
                if (n % ProgressEvery == 0) LogProgress(n, SeedConstants.TotalDevices, sw.ElapsedMilliseconds);
            });

        // Written last. CompletedAt is bookkeeping, the only wall-clock value this service stores.
        await store.WriteMarkerAsync(new SeedMarker(DateTimeOffset.UtcNow, uploaded), ct);

        LogCompleted(uploaded, sw.ElapsedMilliseconds, MaxConcurrency);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "seed already present ({DeviceCount} device blobs, completed {CompletedAt:O}); skipping")]
    private partial void LogAlreadyPresent(int deviceCount, DateTimeOffset completedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "seeded {Uploaded}/{Total} device blobs ({ElapsedMs} ms)")]
    private partial void LogProgress(int uploaded, int total, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "seed completed: {Uploaded} device blobs in {ElapsedMs} ms ({Concurrency} concurrent PUTs)")]
    private partial void LogCompleted(int uploaded, long elapsedMs, int concurrency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "seed attempt {Attempt}/{MaxAttempts} failed")]
    private partial void LogAttemptFailed(Exception ex, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "seeding gave up after {MaxAttempts} attempts; /health stays unhealthy")]
    private partial void LogGaveUp(int maxAttempts);
}
