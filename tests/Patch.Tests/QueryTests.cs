using System.Net;
using System.Text.Json;
using SoR.Patch.Data;
using SoR.Patch.Seeding;
using SoR.Patch.Tests.Support;
using SoR.Shared.Seeding;

namespace SoR.Patch.Tests;

[Collection(MongoCollection.Name)]
[Trait("Category", "Integration")]
public sealed class QueryTests(MongoFixture mongo)
{
    private const string EventsQuery =
        "query($id: ID!) { deviceById(id: $id) { id patchEvents { id deviceId occurredAt status patch { id kbId title severity vendor releasedAt } } } }";

    private const string WindowQuery =
        "query($id: ID!, $since: DateTime, $until: DateTime) { deviceById(id: $id) { id patchEvents(since: $since, until: $until) { id occurredAt } } }";

    private const string PatchesQuery =
        "query($first: Int!, $offset: Int!) { patches(first: $first, offset: $offset) { id kbId title severity vendor releasedAt } }";

    [Fact]
    public async Task Events_for_own_tenant_device()
    {
        var app = await mongo.SeededAppAsync();
        var expected = Expected.NewestFirst(Expected.EventsOf(1));
        var catalog = PatchSeedData.BuildCatalog().ToDictionary(p => p.Id);

        var r = await app.QueryAsync(EventsQuery, Tokens.Alice, new { id = "dev-00001" });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(r.HasErrors, r.ToString());
        var events = Events(r);
        Assert.NotEmpty(events);
        Assert.Equal(expected.Count, events.Count);
        Assert.All(events, e => Assert.StartsWith("dev-00001-p", e.GetProperty("id").GetString(), StringComparison.Ordinal));

        // Every field equals the seed function's output, in the promised order.
        for (var i = 0; i < expected.Count; i++)
        {
            var (want, got) = (expected[i], events[i]);
            var patch = catalog[want.PatchId];
            Assert.Equal(want.Id, got.GetProperty("id").GetString());
            Assert.Equal("dev-00001", got.GetProperty("deviceId").GetString());
            Assert.Equal(new DateTimeOffset(want.OccurredAt), got.GetProperty("occurredAt").GetDateTimeOffset());
            Assert.Equal(want.Status, got.GetProperty("status").GetString());
            var p = got.GetProperty("patch");
            Assert.Equal(patch.Id, p.GetProperty("id").GetString());
            Assert.Equal(patch.KbId, p.GetProperty("kbId").GetString());
            Assert.Equal(patch.Title, p.GetProperty("title").GetString());
            Assert.Equal(patch.Severity, p.GetProperty("severity").GetString());
            Assert.Equal(patch.Vendor, p.GetProperty("vendor").GetString());
            Assert.Equal(new DateTimeOffset(patch.ReleasedAt), p.GetProperty("releasedAt").GetDateTimeOffset());
        }
    }

    [Fact]
    public async Task Events_cross_tenant_are_empty_not_error()
    {
        var app = await mongo.SeededAppAsync();

        var r = await app.QueryAsync(EventsQuery, Tokens.Dave, new { id = "dev-00001" });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(r.HasErrors, r.ToString());
        Assert.Equal("dev-00001", r.Data.GetProperty("deviceById").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Array, r.Data.GetProperty("deviceById").GetProperty("patchEvents").ValueKind);
        Assert.Empty(Events(r));
    }

    [Fact]
    public async Task Tenant_B_patch_only_user_sees_own_tenant_only()
    {
        // erin: TenantB, services ["patch"].
        var app = await mongo.SeededAppAsync();

        var own = await app.QueryAsync(EventsQuery, Tokens.Erin, new { id = "dev-11999" });
        Assert.False(own.HasErrors, own.ToString());
        Assert.Equal(Expected.EventsOf(11_999).Count, Events(own).Count);

        var other = await app.QueryAsync(EventsQuery, Tokens.Erin, new { id = "dev-06999" });
        Assert.False(other.HasErrors, other.ToString());
        Assert.Empty(Events(other));
    }

    [Fact]
    public async Task Denied_without_patch_service()
    {
        // carol: TenantA, services ["softwareinstall"]. The stub device survives; only patchEvents is denied.
        var app = await mongo.SeededAppAsync();

        var r = await app.QueryAsync(EventsQuery, Tokens.Carol, new { id = "dev-00001" });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        var device = r.Data.GetProperty("deviceById");
        Assert.Equal(JsonValueKind.Object, device.ValueKind);
        Assert.Equal("dev-00001", device.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, device.GetProperty("patchEvents").ValueKind);
        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal(["deviceById", "patchEvents"], Path(error));
        Assert.Equal("AUTH_NOT_AUTHORIZED", Code(error));
    }

    [Fact]
    public async Task Denied_on_patches_root_field()
    {
        var app = await mongo.SeededAppAsync();

        var r = await app.QueryAsync("{ patches { id } deviceById(id: \"dev-00001\") { id } }", Tokens.Carol);

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("patches").ValueKind);
        Assert.Equal("dev-00001", r.Data.GetProperty("deviceById").GetProperty("id").GetString());   // sibling unaffected
        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal(["patches"], Path(error));
        Assert.Equal("AUTH_NOT_AUTHORIZED", Code(error));
    }

    [Fact]
    public async Task Unauthenticated_is_AUTH_NOT_AUTHENTICATED()
    {
        var app = await mongo.SeededAppAsync();

        foreach (var token in new[] { null, Tokens.AliceSignedWithOtherKey })
        {
            var lookup = await app.QueryAsync(EventsQuery, token, new { id = "dev-00001" });
            Assert.Equal(JsonValueKind.Null, lookup.Data.GetProperty("deviceById").ValueKind);
            Assert.Equal("AUTH_NOT_AUTHENTICATED", Code(Assert.Single(lookup.Errors.EnumerateArray())));

            var patches = await app.QueryAsync("{ patches { id } }", token);
            Assert.Equal(JsonValueKind.Null, patches.Data.GetProperty("patches").ValueKind);
            Assert.Equal("AUTH_NOT_AUTHENTICATED", Code(patches.Errors[0]));
        }
    }

    [Fact]
    public async Task Since_until_filter_inclusive()
    {
        var app = await mongo.SeededAppAsync();
        var all = Expected.NewestFirst(Expected.EventsOf(7));
        Assert.True(all.Count >= 5);
        // Two seeded timestamps with events strictly outside on both sides (all is newest first).
        var until = all[1].OccurredAt;
        var since = all[^2].OccurredAt;
        var inside = all.Where(e => e.OccurredAt >= since && e.OccurredAt <= until).Select(e => e.Id).ToList();
        Assert.Contains(all, e => e.OccurredAt > until);
        Assert.Contains(all, e => e.OccurredAt < since);

        var r = await app.QueryAsync(WindowQuery, Tokens.Alice, new { id = "dev-00007", since = Iso(since), until = Iso(until) });

        Assert.False(r.HasErrors, r.ToString());
        var ids = Events(r).Select(e => e.GetProperty("id").GetString()!).ToList();
        Assert.Equal(inside, ids);
        Assert.Contains(all[1].Id, ids);    // event exactly at `until`
        Assert.Contains(all[^2].Id, ids);   // event exactly at `since`
        Assert.All(Events(r), e => Assert.InRange(e.GetProperty("occurredAt").GetDateTimeOffset(), new DateTimeOffset(since), new DateTimeOffset(until)));

        // Only one bound, and the bounds given in another offset (same instants).
        var sinceOnly = await app.QueryAsync(WindowQuery, Tokens.Alice, new { id = "dev-00007", since = Iso(since, TimeSpan.FromHours(5)) });
        Assert.Equal(all.Where(e => e.OccurredAt >= since).Select(e => e.Id), Events(sinceOnly).Select(e => e.GetProperty("id").GetString()));
        var untilOnly = await app.QueryAsync(WindowQuery, Tokens.Alice, new { id = "dev-00007", until = Iso(until, TimeSpan.FromHours(-7)) });
        Assert.Equal(all.Where(e => e.OccurredAt <= until).Select(e => e.Id), Events(untilOnly).Select(e => e.GetProperty("id").GetString()));

        // Empty window: since after until.
        var empty = await app.QueryAsync(WindowQuery, Tokens.Alice, new { id = "dev-00007", since = Iso(until), until = Iso(since) });
        Assert.False(empty.HasErrors, empty.ToString());
        Assert.Empty(Events(empty));
    }

    [Fact]
    public async Task Order_is_descending_and_capped()
    {
        var app = await mongo.SeededAppAsync();

        foreach (var index in new[] { 0, 3, 42, 6_999, 7_000, 11_999 })
        {
            var token = DeviceCatalog.TenantOf(index) == SeedConstants.TenantA ? Tokens.Alice : Tokens.Dave;
            var r = await app.QueryAsync(EventsQuery, token, new { id = DeviceCatalog.DeviceId(index) });
            Assert.False(r.HasErrors, r.ToString());

            var events = Events(r);
            var times = events.Select(e => e.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
            Assert.InRange(events.Count, 1, PatchStore.MaxEvents);
            Assert.Equal(times.OrderByDescending(t => t), times);   // non-increasing
            Assert.Equal(Expected.NewestFirst(Expected.EventsOf(index)).Select(e => e.Id), events.Select(e => e.GetProperty("id").GetString()));
        }
    }

    [Fact]
    public async Task Cap_returns_the_newest_1000()
    {
        // No seeded device has more than 30 events, so the cap is exercised on a hand-made database: 1 100 events for
        // one device, one per hour, plus ties at the newest instant to check the id tie-break.
        var t0 = SeedConstants.Epoch.UtcDateTime;
        var events = Enumerable.Range(0, 1_100)
            .Select(n => new PatchEventDocument
            {
                Id = $"dev-00005-p{n:D4}",
                TenantId = SeedConstants.TenantA,
                DeviceId = "dev-00005",
                PatchId = PatchSeedData.PatchId(n % PatchSeedData.CatalogSize),
                Status = "APPLIED",
                OccurredAt = n < 3 ? t0 : t0.AddHours(-n),
            })
            .ToList();
        await using var app = mongo.CreateApp(await mongo.CreateHandMadeDatabaseAsync(events));
        await app.WaitUntilHealthyAsync();

        var r = await app.QueryAsync(WindowQuery, Tokens.Alice, new { id = "dev-00005" });

        Assert.False(r.HasErrors, r.ToString());
        var ids = Events(r).Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(PatchStore.MaxEvents, ids.Count);
        Assert.Equal(Expected.NewestFirst(events).Take(PatchStore.MaxEvents).Select(e => e.Id), ids);
        Assert.Equal(["dev-00005-p0000", "dev-00005-p0001", "dev-00005-p0002"], ids.Take(3));
    }

    [Fact]
    public async Task Lookup_is_a_stub_for_any_id()
    {
        // The lookup never returns null: an id without data (or unknown everywhere) is a stub with no events.
        var app = await mongo.SeededAppAsync();

        var r = await app.QueryAsync(EventsQuery, Tokens.Alice, new { id = "dev-99999" });

        Assert.False(r.HasErrors, r.ToString());
        Assert.Equal("dev-99999", r.Data.GetProperty("deviceById").GetProperty("id").GetString());
        Assert.Empty(Events(r));
    }

    [Fact]
    public async Task Aliased_lookups_in_one_request_succeed()
    {
        // The gateway alias-batches lookups (d0: deviceById(..) d1: ...); they resolve in parallel.
        var app = await mongo.SeededAppAsync();
        var query = string.Concat(Enumerable.Range(0, 20).Select(i =>
            $"d{i}: deviceById(id: \"{DeviceCatalog.DeviceId(i)}\") {{ id patchEvents {{ id patch {{ kbId }} }} }} "));

        var r = await app.QueryAsync($"{{ {query} }}", Tokens.Alice);

        Assert.False(r.HasErrors, r.ToString());
        for (var i = 0; i < 20; i++)
        {
            var events = r.Data.GetProperty($"d{i}").GetProperty("patchEvents");
            Assert.Equal(Expected.EventsOf(i).Count, events.GetArrayLength());
        }
    }

    [Fact]
    public async Task Patches_pages_the_catalog_by_id()
    {
        var app = await mongo.SeededAppAsync();
        var catalog = PatchSeedData.BuildCatalog();

        var page = await Patches(app, first: 3, offset: 10);
        Assert.Equal(["patch-0010", "patch-0011", "patch-0012"], page.Select(p => p.GetProperty("id").GetString()));
        for (var i = 0; i < 3; i++)
        {
            var (want, got) = (catalog[10 + i], page[i]);
            Assert.Equal(want.KbId, got.GetProperty("kbId").GetString());
            Assert.Equal(want.Title, got.GetProperty("title").GetString());
            Assert.Equal(want.Severity, got.GetProperty("severity").GetString());
            Assert.Equal(want.Vendor, got.GetProperty("vendor").GetString());
            Assert.Equal(new DateTimeOffset(want.ReleasedAt), got.GetProperty("releasedAt").GetDateTimeOffset());
        }

        var defaults = await app.QueryAsync("{ patches { id } }", Tokens.Erin);
        Assert.False(defaults.HasErrors, defaults.ToString());
        Assert.Equal(catalog.Take(25).Select(p => p.Id), defaults.Data.GetProperty("patches").EnumerateArray().Select(p => p.GetProperty("id").GetString()));

        Assert.Equal(100, (await Patches(app, first: 500, offset: 0)).Count);
        Assert.Single(await Patches(app, first: 0, offset: 0));
        Assert.Equal("patch-0000", (await Patches(app, first: 1, offset: -5))[0].GetProperty("id").GetString());
        Assert.Equal(["patch-0299"], (await Patches(app, first: 10, offset: 299)).Select(p => p.GetProperty("id").GetString()));
        Assert.Empty(await Patches(app, first: 10, offset: 300));
    }

    private static async Task<List<JsonElement>> Patches(PatchApp app, int first, int offset)
    {
        var r = await app.QueryAsync(PatchesQuery, Tokens.Alice, new { first, offset });
        Assert.False(r.HasErrors, r.ToString());
        return [.. r.Data.GetProperty("patches").EnumerateArray()];
    }

    private static List<JsonElement> Events(GraphQLResponse r) =>
        [.. r.Data.GetProperty("deviceById").GetProperty("patchEvents").EnumerateArray()];

    private static string Iso(DateTime utc, TimeSpan? offset = null) =>
        new DateTimeOffset(utc).ToOffset(offset ?? TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture);

    private static string[] Path(JsonElement error) => [.. error.GetProperty("path").EnumerateArray().Select(p => p.ToString())];

    private static string? Code(JsonElement error) =>
        error.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var code) ? code.GetString() : null;
}
