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

    private const string TimelineQuery = """
        query($id: ID!, $since: DateTime, $until: DateTime) {
          deviceById(id: $id) {
            id
            patchTimeline(since: $since, until: $until) {
              id occurredAt label title subtitle status severity
              details { label value mono }
            }
          }
        }
        """;

    private const string PatchesQuery =
        "query($search: String, $first: Int!, $offset: Int!) { patches(search: $search, first: $first, offset: $offset) { id kbId title severity vendor releasedAt } }";

    private const string FindQuery = """
        query($ids: [ID!]!, $deviceIds: [ID!], $first: Int!, $offset: Int!) {
          devicesWithPatches(patchIds: $ids, deviceIds: $deviceIds, first: $first, offset: $offset) {
            totalCount
            items { device { id } events { id deviceId occurredAt status patch { id kbId } } }
          }
        }
        """;

    private const string MatchesQuery = """
        query($ids: [ID!]!) { devicesWithPatches(patchIds: $ids, first: 1) { totalCount matches { patchId deviceIds } } }
        """;

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
    public async Task PatchTimeline_projects_complete_normalized_events()
    {
        var app = await mongo.SeededAppAsync();
        var expected = Expected.NewestFirst(Expected.EventsOf(1));
        var catalog = PatchSeedData.BuildCatalog().ToDictionary(p => p.Id);

        var r = await app.QueryAsync(TimelineQuery, Tokens.Alice, new { id = "dev-00001" });

        Assert.False(r.HasErrors, r.ToString());
        var events = TimelineEvents(r, "patchTimeline");
        Assert.Equal(expected.Select(e => e.Id), events.Select(e => e.GetProperty("id").GetString()));
        foreach (var (want, got) in expected.Zip(events))
        {
            var patch = catalog[want.PatchId];
            Assert.Equal(new DateTimeOffset(want.OccurredAt), got.GetProperty("occurredAt").GetDateTimeOffset());
            Assert.Equal(patch.KbId, got.GetProperty("label").GetString());
            Assert.Equal(patch.Title, got.GetProperty("title").GetString());
            Assert.Equal($"{patch.KbId} · {patch.Vendor}", got.GetProperty("subtitle").GetString());
            Assert.Equal(want.Status, got.GetProperty("status").GetString());
            Assert.Equal(patch.Severity, got.GetProperty("severity").GetString());
            AssertDetails(got,
                ("KB", patch.KbId, true),
                ("Vendor", patch.Vendor, false),
                ("Severity", patch.Severity, false),
                ("Status", want.Status, false),
                ("Patch ID", patch.Id, true),
                ("Event ID", want.Id, true));
        }
    }

    [Fact]
    public async Task PatchTimeline_preserves_date_tenant_and_authorization_rules()
    {
        var app = await mongo.SeededAppAsync();
        var all = Expected.NewestFirst(Expected.EventsOf(7));
        var since = all[^2].OccurredAt;
        var until = all[1].OccurredAt;

        var window = await app.QueryAsync(TimelineQuery, Tokens.Alice,
            new { id = "dev-00007", since = Iso(since), until = Iso(until) });
        Assert.False(window.HasErrors, window.ToString());
        Assert.Equal(all.Where(e => e.OccurredAt >= since && e.OccurredAt <= until).Select(e => e.Id),
            TimelineEvents(window, "patchTimeline").Select(e => e.GetProperty("id").GetString()));

        var crossTenant = await app.QueryAsync(TimelineQuery, Tokens.Dave, new { id = "dev-00001" });
        Assert.False(crossTenant.HasErrors, crossTenant.ToString());
        Assert.Empty(TimelineEvents(crossTenant, "patchTimeline"));

        var denied = await app.QueryAsync(TimelineQuery, Tokens.Carol, new { id = "dev-00001" });
        Assert.Equal(JsonValueKind.Null, denied.Data.GetProperty("deviceById").GetProperty("patchTimeline").ValueKind);
        var error = Assert.Single(denied.Errors.EnumerateArray());
        Assert.Equal(["deviceById", "patchTimeline"], Path(error));
        Assert.Equal("AUTH_NOT_AUTHORIZED", Code(error));
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

    [Fact]
    public async Task Patches_search_matches_kb_id_and_title_case_insensitively()
    {
        var app = await mongo.SeededAppAsync();
        var catalog = PatchSeedData.BuildCatalog();

        // A KB fragment: every id whose kbId contains it, in id order.
        var byKb = await Patches(app, first: 100, offset: 0, search: "kb50001");
        var wantKb = catalog.Where(p => p.KbId.Contains("KB50001", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToList();
        Assert.NotEmpty(wantKb);
        Assert.Equal(wantKb, byKb.Select(p => p.GetProperty("id").GetString()));

        // A vendor word from the title, mixed case.
        var byTitle = await Patches(app, first: 100, offset: 0, search: "aPPle");
        var wantTitle = catalog.Where(p => p.Title.Contains("Apple", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).Take(100).ToList();
        Assert.NotEmpty(wantTitle);
        Assert.Equal(wantTitle, byTitle.Select(p => p.GetProperty("id").GetString()));

        // Regex metacharacters are literal, blank means no filter.
        Assert.Empty(await Patches(app, first: 10, offset: 0, search: ".*"));
        Assert.Equal(catalog.Take(10).Select(p => p.Id), (await Patches(app, first: 10, offset: 0, search: "   ")).Select(p => p.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Devices_with_patches_pages_the_matching_devices_with_their_events()
    {
        // Two catalog patches: the devices of TenantA with an event for either, by device id, each with those events
        // newest first (contract). The count is unpaged; the page is stable across offsets.
        var app = await mongo.SeededAppAsync();
        var ids = new[] { PatchSeedData.PatchId(7), PatchSeedData.PatchId(42) };
        var expected = Expected.DevicesWith(SeedConstants.TenantA, ids);
        Assert.InRange(expected.Count, 10, 5_000);

        var page = await Find(app, Tokens.Alice, ids, first: 25, offset: 0);
        Assert.Equal(expected.Count, page.GetProperty("totalCount").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(25, items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var (want, got) = (expected[i], items[i]);
            Assert.Equal(want.DeviceId, got.GetProperty("device").GetProperty("id").GetString());
            var events = got.GetProperty("events").EnumerateArray().ToList();
            Assert.Equal(want.Events.Select(e => e.Id), events.Select(e => e.GetProperty("id").GetString()));
            Assert.All(events, e => Assert.Equal(want.DeviceId, e.GetProperty("deviceId").GetString()));
            Assert.All(events, e => Assert.Contains(e.GetProperty("patch").GetProperty("id").GetString(), ids));
            foreach (var (w, g) in want.Events.Zip(events))
            {
                Assert.Equal(w.Status, g.GetProperty("status").GetString());
                Assert.Equal(new DateTimeOffset(w.OccurredAt), g.GetProperty("occurredAt").GetDateTimeOffset());
            }
        }

        var second = await Find(app, Tokens.Alice, ids, first: 25, offset: 25);
        Assert.Equal(expected.Skip(25).Take(25).Select(x => x.DeviceId), DeviceIds(second));
        var last = await Find(app, Tokens.Alice, ids, first: 100, offset: expected.Count - 3);
        Assert.Equal(expected.TakeLast(3).Select(x => x.DeviceId), DeviceIds(last));
        Assert.Empty(DeviceIds(await Find(app, Tokens.Alice, ids, first: 25, offset: expected.Count)));

        // Duplicate and blank ids collapse; clamping as everywhere else (first 1..100, offset >= 0).
        var messy = await Find(app, Tokens.Alice, [ids[0], ids[0], " ", ids[1]], first: 500, offset: -4);
        Assert.Equal(expected.Count, messy.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected.Take(100).Select(x => x.DeviceId), DeviceIds(messy));
    }

    [Fact]
    public async Task Matches_give_every_selected_patch_its_sorted_device_set()
    {
        // The per-patch device sets (for AND / OR across selections and subgraphs): each equals the seed's answer for
        // that patch alone, sorted; an unknown id gets an empty set; the union is the lookup's own total.
        var app = await mongo.SeededAppAsync();
        var ids = new[] { PatchSeedData.PatchId(7), "patch-9999", PatchSeedData.PatchId(42) };

        var r = await app.QueryAsync(MatchesQuery, Tokens.Alice, new { ids });

        Assert.False(r.HasErrors, r.ToString());
        var result = r.Data.GetProperty("devicesWithPatches");
        var matches = result.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(ids, matches.Select(m => m.GetProperty("patchId").GetString()));
        var sets = matches.ToDictionary(m => m.GetProperty("patchId").GetString()!, m => m.GetProperty("deviceIds").EnumerateArray().Select(d => d.GetString()!).ToList());
        Assert.Empty(sets["patch-9999"]);
        foreach (var id in new[] { ids[0], ids[2] })
        {
            var expected = Expected.DevicesWith(SeedConstants.TenantA, [id]).Select(x => x.DeviceId).ToList();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, sets[id]);
        }

        Assert.Equal(sets[ids[0]].Union(sets[ids[2]]).Count(), result.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Device_ids_restrict_the_candidates_to_a_computed_page()
    {
        // A client intersects two sets itself, then asks for the events of exactly those devices.
        var app = await mongo.SeededAppAsync();
        var a = Expected.DevicesWith(SeedConstants.TenantA, [PatchSeedData.PatchId(7)]).Select(x => x.DeviceId).ToHashSet();
        var b = Expected.DevicesWith(SeedConstants.TenantA, [PatchSeedData.PatchId(42)]).Select(x => x.DeviceId).ToHashSet();
        var both = a.Intersect(b).Order(StringComparer.Ordinal).ToList();
        var neither = DeviceCatalog.ForTenant(SeedConstants.TenantA).Select(d => d.Id).First(id => !a.Contains(id) && !b.Contains(id));
        var page = both.Concat([neither, "dev-11999"]).ToArray();   // + a TenantA device with neither patch and a TenantB device
        Assert.InRange(both.Count, 1, 100);

        var r = await app.QueryAsync(FindQuery, Tokens.Alice, new { ids = new[] { PatchSeedData.PatchId(7), PatchSeedData.PatchId(42) }, deviceIds = page, first = 100, offset = 0 });

        Assert.False(r.HasErrors, r.ToString());
        var result = r.Data.GetProperty("devicesWithPatches");
        Assert.Equal(both.Count, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(both, DeviceIds(result));
        Assert.All(result.GetProperty("items").EnumerateArray(), i => Assert.NotEmpty(i.GetProperty("events").EnumerateArray()));

        // An empty restriction matches nothing; more than 100 ids is an error, like a too-large selection.
        var none = await app.QueryAsync(FindQuery, Tokens.Alice, new { ids = new[] { PatchSeedData.PatchId(7) }, deviceIds = Array.Empty<string>(), first = 25, offset = 0 });
        Assert.False(none.HasErrors, none.ToString());
        Assert.Equal(0, none.Data.GetProperty("devicesWithPatches").GetProperty("totalCount").GetInt32());
        var tooMany = await app.QueryAsync(FindQuery, Tokens.Alice, new { ids = new[] { PatchSeedData.PatchId(7) }, deviceIds = Enumerable.Range(0, 101).Select(DeviceCatalog.DeviceId).ToArray(), first = 25, offset = 0 });
        Assert.Equal("SELECTION_TOO_LARGE", Code(Assert.Single(tooMany.Errors.EnumerateArray())));
    }

    [Fact]
    public async Task Devices_with_patches_is_scoped_to_the_callers_tenant()
    {
        var app = await mongo.SeededAppAsync();
        var ids = new[] { PatchSeedData.PatchId(3) };

        var dave = await Find(app, Tokens.Dave, ids, first: 100, offset: 0);
        var expectedB = Expected.DevicesWith(SeedConstants.TenantB, ids);
        Assert.Equal(expectedB.Count, dave.GetProperty("totalCount").GetInt32());
        Assert.Equal(expectedB.Take(100).Select(x => x.DeviceId), DeviceIds(dave));
        Assert.All(DeviceIds(dave), id => Assert.Equal(SeedConstants.TenantB, DeviceCatalog.TenantOf(int.Parse(id[4..], System.Globalization.CultureInfo.InvariantCulture))));

        // erin: TenantB, patch only. Same tenant, same answer.
        var erin = await Find(app, Tokens.Erin, ids, first: 100, offset: 0);
        Assert.Equal(DeviceIds(dave), DeviceIds(erin));
    }

    [Fact]
    public async Task Devices_with_patches_empty_or_unknown_selection_matches_nothing()
    {
        var app = await mongo.SeededAppAsync();

        foreach (var ids in new[] { Array.Empty<string>(), [""], ["patch-9999", "nope"] })
        {
            var r = await Find(app, Tokens.Alice, ids, first: 25, offset: 0);
            Assert.Equal(0, r.GetProperty("totalCount").GetInt32());
            Assert.Empty(r.GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public async Task Devices_with_patches_rejects_more_than_50_ids()
    {
        var app = await mongo.SeededAppAsync();
        var ids = Enumerable.Range(0, 51).Select(PatchSeedData.PatchId).ToArray();

        var r = await app.QueryAsync(FindQuery, Tokens.Alice, new { ids, first = 25, offset = 0 });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("devicesWithPatches").ValueKind);
        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal(["devicesWithPatches"], Path(error));
        Assert.Equal("SELECTION_TOO_LARGE", Code(error));
        Assert.Contains("at most 50", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        var exactly50 = await Find(app, Tokens.Alice, ids[..50], first: 1, offset: 0);
        Assert.True(exactly50.GetProperty("totalCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Devices_with_patches_denied_without_patch_service()
    {
        // carol: TenantA, softwareinstall only. The sibling root field is unaffected.
        var app = await mongo.SeededAppAsync();

        var r = await app.QueryAsync(
            "{ devicesWithPatches(patchIds: [\"patch-0001\"]) { totalCount } deviceById(id: \"dev-00001\") { id } }", Tokens.Carol);

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("devicesWithPatches").ValueKind);
        Assert.Equal("dev-00001", r.Data.GetProperty("deviceById").GetProperty("id").GetString());
        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal(["devicesWithPatches"], Path(error));
        Assert.Equal("AUTH_NOT_AUTHORIZED", Code(error));
    }

    private static async Task<JsonElement> Find(PatchApp app, string token, string[] ids, int first, int offset)
    {
        var r = await app.QueryAsync(FindQuery, token, new { ids, deviceIds = (string[]?)null, first, offset });
        Assert.False(r.HasErrors, r.ToString());
        return r.Data.GetProperty("devicesWithPatches");
    }

    private static List<string> DeviceIds(JsonElement matches) =>
        [.. matches.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("device").GetProperty("id").GetString()!)];

    private static async Task<List<JsonElement>> Patches(PatchApp app, int first, int offset, string? search = null)
    {
        var r = await app.QueryAsync(PatchesQuery, Tokens.Alice, new { search, first, offset });
        Assert.False(r.HasErrors, r.ToString());
        return [.. r.Data.GetProperty("patches").EnumerateArray()];
    }

    private static List<JsonElement> Events(GraphQLResponse r) =>
        [.. r.Data.GetProperty("deviceById").GetProperty("patchEvents").EnumerateArray()];

    private static List<JsonElement> TimelineEvents(GraphQLResponse r, string field) =>
        [.. r.Data.GetProperty("deviceById").GetProperty(field).EnumerateArray()];

    private static void AssertDetails(JsonElement timelineEvent, params (string Label, string Value, bool Mono)[] expected)
    {
        var details = timelineEvent.GetProperty("details").EnumerateArray().ToList();
        Assert.Equal(expected.Length, details.Count);
        foreach (var (want, got) in expected.Zip(details))
        {
            Assert.Equal(want.Label, got.GetProperty("label").GetString());
            Assert.Equal(want.Value, got.GetProperty("value").GetString());
            Assert.Equal(want.Mono, got.GetProperty("mono").GetBoolean());
        }
    }

    private static string Iso(DateTime utc, TimeSpan? offset = null) =>
        new DateTimeOffset(utc).ToOffset(offset ?? TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture);

    private static string[] Path(JsonElement error) => [.. error.GetProperty("path").EnumerateArray().Select(p => p.ToString())];

    private static string? Code(JsonElement error) =>
        error.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var code) ? code.GetString() : null;
}
