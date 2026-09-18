using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using SoR.Patch.Data;
using SoR.Patch.Seeding;
using SoR.Patch.Tests.Support;
using SoR.Shared.Seeding;
using Xunit.Abstractions;

namespace SoR.Patch.Tests;

[Collection(MongoCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SeedingTests(MongoFixture mongo, ITestOutputHelper output)
{
    [Fact]
    public async Task Health_is_503_until_seeded_then_200()
    {
        var app = await mongo.SeededAppAsync();

        var observed = mongo.SeedHealthObservations;
        output.WriteLine($"/health observed: {string.Join(", ", observed.GroupBy(s => s).Select(g => $"{(int)g.Key} x{g.Count()}"))}; healthy after {mongo.TimeUntilHealthy.TotalSeconds:F1} s");
        output.WriteLine(Assert.Single(app.Logs.Messages, m => m.Contains("seed completed:", StringComparison.Ordinal)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, observed[0]);
        Assert.Equal(HttpStatusCode.OK, observed[^1]);
        Assert.All(observed.SkipLast(1), s => Assert.Equal(HttpStatusCode.ServiceUnavailable, s));
        Assert.True(mongo.TimeUntilHealthy < TimeSpan.FromSeconds(180), $"seeding took {mongo.TimeUntilHealthy}");
    }

    [Fact]
    public async Task Seed_is_idempotent_and_indexed()
    {
        var first = await mongo.SeededAppAsync();
        var db = mongo.Database();
        var before = await CountsAsync(db);
        var marker = await Marker(db);
        Assert.Equal(Expected.TotalEvents, before.Events);
        Assert.Equal(PatchSeedData.CatalogSize, before.Patches);
        Assert.Equal(Expected.TotalEvents, marker.Count);
        Assert.Contains(first.Logs.Messages, m => m.Contains("seeded ", StringComparison.Ordinal));

        // Second start on the same database: the marker is found, nothing is dropped or reseeded.
        await using (var second = mongo.CreateApp(MongoFixture.SharedDatabase))
        {
            await second.WaitUntilHealthyAsync();
            output.WriteLine(Assert.Single(second.Logs.Messages, m => m.Contains("seed already present", StringComparison.Ordinal)));
            Assert.DoesNotContain(second.Logs.Messages, m => m.Contains("seeded ", StringComparison.Ordinal));
        }

        Assert.Equal(before, await CountsAsync(db));
        var markerAgain = await Marker(db);
        Assert.Equal(marker.CompletedAt, markerAgain.CompletedAt);
        Assert.Equal(marker.Count, markerAgain.Count);

        var indexes = await (await db.GetCollection<BsonDocument>(PatchEventDocument.Collection).Indexes.ListAsync()).ToListAsync();
        var names = indexes.Select(i => i["name"].AsString).ToList();
        Assert.Contains("tenantId_1_deviceId_1_occurredAt_-1", names);
        Assert.Contains("tenantId_1", names);
        var timeline = indexes.Single(i => i["name"] == "tenantId_1_deviceId_1_occurredAt_-1");
        Assert.Equal(new BsonDocument { { "tenantId", 1 }, { "deviceId", 1 }, { "occurredAt", -1 } }, timeline["key"].AsBsonDocument);
    }

    [Fact]
    public async Task Seeded_documents_equal_the_seed_functions()
    {
        // Determinism end to end: every stored event and patch is exactly what the pure functions produce.
        await mongo.SeededAppAsync();
        var db = mongo.Database();

        var expectedEvents = DeviceCatalog.All().SelectMany(d => PatchSeedData.BuildEventsFor(d)).ToDictionary(e => e.Id);
        var seen = 0;
        using (var cursor = await db.GetCollection<PatchEventDocument>(PatchEventDocument.Collection)
                   .FindAsync(FilterDefinition<PatchEventDocument>.Empty, new FindOptions<PatchEventDocument> { BatchSize = 10_000 }))
        {
            while (await cursor.MoveNextAsync())
            {
                foreach (var e in cursor.Current)
                {
                    Assert.True(expectedEvents.TryGetValue(e.Id, out var want), $"unexpected event {e.Id}");
                    Assert.Equal((want.TenantId, want.DeviceId, want.PatchId, want.Status, want.OccurredAt),
                                 (e.TenantId, e.DeviceId, e.PatchId, e.Status, e.OccurredAt));
                    seen++;
                }
            }
        }

        Assert.Equal(expectedEvents.Count, seen);

        var patches = await db.GetCollection<PatchDocument>(PatchDocument.Collection)
            .Find(FilterDefinition<PatchDocument>.Empty).SortBy(p => p.Id).ToListAsync();
        Assert.Equal(
            PatchSeedData.BuildCatalog().Select(p => (p.Id, p.KbId, p.Title, p.Severity, p.Vendor, p.ReleasedAt)),
            patches.Select(p => (p.Id, p.KbId, p.Title, p.Severity, p.Vendor, p.ReleasedAt)));

        var events = db.GetCollection<PatchEventDocument>(PatchEventDocument.Collection);
        Assert.Equal(Expected.EventsOfTenant(SeedConstants.TenantA), await events.CountDocumentsAsync(e => e.TenantId == SeedConstants.TenantA));
        Assert.Equal(Expected.EventsOfTenant(SeedConstants.TenantB), await events.CountDocumentsAsync(e => e.TenantId == SeedConstants.TenantB));
    }

    [Fact]
    public async Task Partial_seed_without_marker_is_discarded_and_redone()
    {
        // A crash mid-seed: some events and patches written, no marker, plus documents no seed would write.
        var name = mongo.NewDatabaseName();
        var db = mongo.Database(name);
        var partial = DeviceCatalog.All().Take(100).SelectMany(d => PatchSeedData.BuildEventsFor(d)).ToList();
        partial.Add(new PatchEventDocument
        {
            Id = "stray", TenantId = SeedConstants.TenantA, DeviceId = "dev-00001", PatchId = "patch-9999", Status = "APPLIED",
            OccurredAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.GetCollection<PatchEventDocument>(PatchEventDocument.Collection).InsertManyAsync(partial);
        await db.GetCollection<PatchDocument>(PatchDocument.Collection).InsertManyAsync(PatchSeedData.BuildCatalog().Take(10));

        await using var app = mongo.CreateApp(name);
        await app.WaitUntilHealthyAsync();
        output.WriteLine(Assert.Single(app.Logs.Messages, m => m.Contains("seed completed:", StringComparison.Ordinal)));

        var counts = await CountsAsync(db);
        Assert.Equal(Expected.TotalEvents, counts.Events);
        Assert.Equal(PatchSeedData.CatalogSize, counts.Patches);
        Assert.Equal(0, await db.GetCollection<PatchEventDocument>(PatchEventDocument.Collection).CountDocumentsAsync(e => e.Id == "stray"));
        Assert.Equal(Expected.TotalEvents, (await Marker(db)).Count);

        // Dropping discarded the indexes too; they must have been recreated.
        var indexes = await (await db.GetCollection<BsonDocument>(PatchEventDocument.Collection).Indexes.ListAsync()).ToListAsync();
        Assert.Contains(indexes, i => i["name"] == "tenantId_1_deviceId_1_occurredAt_-1");
    }

    [Fact]
    public async Task Health_is_unhealthy_without_signing_key()
    {
        await mongo.SeededAppAsync();   // data is seeded; only the key is missing
        await using var app = new PatchApp(mongo.ConnectionString, MongoFixture.SharedDatabase, signingKey: null);
        var health = app.Services.GetRequiredService<HealthCheckService>();

        // Wait for this instance's seed check (it finds the marker quickly), then /health must still be 503.
        HealthReport report;
        var deadline = DateTime.UtcNow.AddMinutes(1);
        do
        {
            report = await health.CheckHealthAsync();
            if (report.Entries["seed"].Status == HealthStatus.Healthy) break;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(HealthStatus.Healthy, report.Entries["seed"].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["mongo"].Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["auth-config"].Status);

        using var client = app.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static async Task<(long Events, long Patches)> CountsAsync(IMongoDatabase db) =>
        (await db.GetCollection<PatchEventDocument>(PatchEventDocument.Collection).CountDocumentsAsync(FilterDefinition<PatchEventDocument>.Empty),
         await db.GetCollection<PatchDocument>(PatchDocument.Collection).CountDocumentsAsync(FilterDefinition<PatchDocument>.Empty));

    private static async Task<SeedStateDocument> Marker(IMongoDatabase db) =>
        await db.GetCollection<SeedStateDocument>(SeedStateDocument.Collection).Find(m => m.Key == SeedStateDocument.PatchKey).SingleAsync();
}
