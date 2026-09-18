using System.Diagnostics;
using System.Net;
using MongoDB.Driver;
using SoR.Patch.Data;
using SoR.Patch.Seeding;
using Testcontainers.MongoDb;

namespace SoR.Patch.Tests.Support;

[CollectionDefinition(Name)]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>
{
    public const string Name = "Mongo";
}

/// <summary>
/// One <c>mongo:8</c> per test run, configured like compose (no auth, 0.25 GB WiredTiger cache). Database
/// <c>patch</c> backs one shared app, seeded once (about 210 000 events) and reused by every test class in the
/// collection. Tests that need an empty or hand-made database use <see cref="NewDatabaseName"/>.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    public const string Image = "mongo:8";
    public const string SharedDatabase = MongoOptions.DefaultDatabase;

    private readonly MongoDbContainer _container = new MongoDbBuilder(Image)
        .WithUsername(string.Empty)   // both empty = no auth, like the compose service
        .WithPassword(string.Empty)
        .WithCommand("--wiredTigerCacheSizeGB", "0.25")
        .Build();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private PatchApp? _seededApp;
    private MongoClient? _client;
    private int _databases;

    public string ConnectionString => _container.GetConnectionString();

    public IMongoClient Client => _client ?? throw new InvalidOperationException("fixture not initialised");

    /// <summary>Every <c>/health</c> status the shared app returned while it seeded, in order.</summary>
    public IReadOnlyList<HttpStatusCode> SeedHealthObservations { get; private set; } = [];

    /// <summary>Wall time from app start until <c>/health</c> first answered 200.</summary>
    public TimeSpan TimeUntilHealthy { get; private set; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _client = new MongoClient(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_seededApp is not null) await _seededApp.DisposeAsync();
        _client?.Dispose();
        await _container.DisposeAsync();
    }

    public IMongoDatabase Database(string name = SharedDatabase) => Client.GetDatabase(name);

    public string NewDatabaseName() => $"patch_test_{Interlocked.Increment(ref _databases)}";

    public PatchApp CreateApp(string database, string? signingKey = null) =>
        new(ConnectionString, database, signingKey ?? Tokens.SigningKey);

    /// <summary>The shared app on <see cref="SharedDatabase"/>, started once and seeded (/health 200).</summary>
    public async Task<PatchApp> SeededAppAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_seededApp is null)
            {
                var app = CreateApp(SharedDatabase);
                var sw = Stopwatch.StartNew();
                SeedHealthObservations = await app.WaitUntilHealthyAsync();
                TimeUntilHealthy = sw.Elapsed;
                _seededApp = app;
            }

            return _seededApp;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A database that looks already seeded (marker + catalog) but holds only <paramref name="events"/>, so an
    /// app started on it skips seeding and serves exactly these events.
    /// </summary>
    public async Task<string> CreateHandMadeDatabaseAsync(IReadOnlyList<PatchEventDocument> events)
    {
        var name = NewDatabaseName();
        var db = Database(name);
        await db.GetCollection<PatchDocument>(PatchDocument.Collection).InsertManyAsync(PatchSeedData.BuildCatalog());
        if (events.Count > 0) await db.GetCollection<PatchEventDocument>(PatchEventDocument.Collection).InsertManyAsync(events);
        await db.GetCollection<SeedStateDocument>(SeedStateDocument.Collection).InsertOneAsync(
            new SeedStateDocument { Key = SeedStateDocument.PatchKey, CompletedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), Count = events.Count });
        return name;
    }
}
