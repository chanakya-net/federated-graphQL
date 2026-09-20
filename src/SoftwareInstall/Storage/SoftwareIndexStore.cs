using SoR.SoftwareInstall.Seeding;

namespace SoR.SoftwareInstall.Storage;

public interface ISoftwareIndexStore
{
    /// <summary>The index, read from the blob on first use (after the seed is complete) and kept in memory.</summary>
    Task<SoftwareIndex> GetAsync(CancellationToken ct);
}

/// <summary>
/// Reads <c>_index/software.json</c> once the seed is complete and caches it for the life of the process (the data is
/// seeded once and never changes; a restart re-reads it). Before the marker exists nothing is cached, so a query
/// during seeding fails with a field error rather than pinning a partial index.
/// </summary>
public sealed class SoftwareIndexStore(InstallEventsBlobStore store, SeedState seed) : ISoftwareIndexStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SoftwareIndex? _index;

    public async Task<SoftwareIndex> GetAsync(CancellationToken ct)
    {
        if (_index is { } cached) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_index is { } loadedMeanwhile) return loadedMeanwhile;

            var document = await store.ReadIndexAsync(ct)
                ?? throw new InvalidOperationException($"software index blob {InstallEventsBlobStore.IndexBlobName} is missing; it is written by the seeder");
            var index = new SoftwareIndex(document);
            if (seed.Completed) _index = index;
            return index;
        }
        finally
        {
            _gate.Release();
        }
    }
}
