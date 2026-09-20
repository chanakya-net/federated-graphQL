using System.Net;
using System.Text.Json;
using SoR.Shared.Seeding;
using SoR.SoftwareInstall.Seeding;
using SoR.SoftwareInstall.Storage;
using SoR.SoftwareInstall.Tests.Support;

namespace SoR.SoftwareInstall.Tests;

/// <summary>
/// The gateway's view of this subgraph: <c>deviceById(id)</c> (internal lookup) and <c>installEvents</c> on it,
/// against the shared seeded Azurite container.
/// </summary>
[Collection(AzuriteCollection.Name)]
[Trait("Category", "Integration")]
public sealed class QueryTests(AzuriteFixture azurite)
{
    private const string EventsQuery = """
        query($id: ID!, $since: DateTime, $until: DateTime) {
          deviceById(id: $id) {
            id
            installEvents(since: $since, until: $until) { id deviceId occurredAt action result software { name version publisher } }
          }
        }
        """;

    private const string SoftwareQuery =
        "query($search: String, $first: Int!, $offset: Int!) { software(search: $search, first: $first, offset: $offset) { name version publisher } }";

    private const string FindQuery = """
        query($software: [SoftwareKeyInput!]!, $deviceIds: [ID!], $first: Int!, $offset: Int!) {
          devicesWithSoftware(software: $software, deviceIds: $deviceIds, first: $first, offset: $offset) {
            totalCount
            items { device { id } events { id deviceId occurredAt action result software { name version publisher } } }
          }
        }
        """;

    private const string MatchesQuery = """
        query($software: [SoftwareKeyInput!]!) { devicesWithSoftware(software: $software, first: 1) { totalCount matches { name version deviceIds } } }
        """;

    private static readonly Lazy<SoftwareIndex> ExpectedIndex = new(() =>
        new SoftwareIndex(SoftwareIndexBuilder.Build(DeviceCatalog.All().Select(InstallSeedData.BuildDocument))));

    private SoftwareInstallApp App => azurite.App;

    [Fact]
    public async Task Events_for_own_tenant_device()
    {
        var r = await App.QueryAsync(EventsQuery, Tokens.Alice, new { id = "dev-00001" });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(r.HasErrors, r.ToString());
        var events = Events(r);
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.StartsWith("dev-00001-i", e.GetProperty("id").GetString(), StringComparison.Ordinal));
        AssertDescending(events);
        AssertEqualsSeed(DeviceCatalog.Build(1), events);
    }

    [Fact]
    public async Task Events_for_own_tenant_B_device()
    {
        var r = await App.QueryAsync(EventsQuery, Tokens.Dave, new { id = "dev-07000" });

        Assert.False(r.HasErrors, r.ToString());
        AssertEqualsSeed(DeviceCatalog.Build(7_000), Events(r));
    }

    [Theory]
    [InlineData("dave", "dev-00001")]    // TenantB asking for a TenantA device
    [InlineData("alice", "dev-07000")]   // TenantA asking for a TenantB device
    public async Task Events_cross_tenant_are_empty_not_error(string user, string deviceId)
    {
        var otherTenantsBlob = InstallEventsBlobStore.BlobName(user == "dave" ? "TenantB" : "TenantA", deviceId);
        Assert.False((await azurite.Container.GetBlobClient(otherTenantsBlob).ExistsAsync()).Value);   // structurally absent

        var r = await App.QueryAsync(EventsQuery, Tokens.For(user), new { id = deviceId });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(r.HasErrors, r.ToString());
        Assert.Equal(deviceId, r.Data.GetProperty("deviceById").GetProperty("id").GetString());
        Assert.Empty(Events(r));
    }

    [Fact]
    public async Task Missing_blob_yields_empty_list()
    {
        // Destructive: deletes dev-00005's blob (no other test reads dev-00005) and always puts it back, so the
        // shared container stays fully seeded whatever runs next.
        var device = DeviceCatalog.Build(5);
        var blob = azurite.Container.GetBlobClient(InstallEventsBlobStore.BlobName(device.TenantId, device.Id));
        Assert.True((await blob.DeleteIfExistsAsync()).Value);
        try
        {
            var r = await App.QueryAsync(EventsQuery, Tokens.Alice, new { id = device.Id });

            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.False(r.HasErrors, r.ToString());
            Assert.Empty(Events(r));
        }
        finally
        {
            await blob.UploadAsync(InstallJson.Serialize(InstallSeedData.BuildDocument(device)), overwrite: true);
        }

        var restored = await App.QueryAsync(EventsQuery, Tokens.Alice, new { id = device.Id });
        AssertEqualsSeed(device, Events(restored));
    }

    [Theory]
    [InlineData("bob", "dev-00001")]    // TenantA, patch + vulnerability
    [InlineData("erin", "dev-07000")]   // TenantB, patch only
    public async Task Denied_without_softwareinstall_service(string user, string deviceId)
    {
        var r = await App.QueryAsync(EventsQuery, Tokens.For(user), new { id = deviceId });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        var device = r.Data.GetProperty("deviceById");
        Assert.Equal(JsonValueKind.Object, device.ValueKind);   // the parent survives: nullable field
        Assert.Equal(deviceId, device.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, device.GetProperty("installEvents").ValueKind);

        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal(["deviceById", "installEvents"], error.GetProperty("path").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("AUTH_NOT_AUTHORIZED", error.GetProperty("extensions").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Allowed_with_only_the_softwareinstall_service()
    {
        var r = await App.QueryAsync(EventsQuery, Tokens.Carol, new { id = "dev-00001" });

        Assert.False(r.HasErrors, r.ToString());
        AssertEqualsSeed(DeviceCatalog.Build(1), Events(r));
    }

    [Fact]
    public async Task Since_until_filter_inclusive()
    {
        // A TenantA device with enough distinct timestamps to cut a window with events on both sides.
        var device = DeviceCatalog.ForTenant(SeedConstants.TenantA)
            .First(d => InstallSeedData.BuildEventsFor(d) is { Count: >= 8 } ev && ev.Select(e => e.OccurredAt).Distinct().Count() == ev.Count);
        var seeded = InstallSeedData.BuildEventsFor(device);   // ascending
        var since = seeded[2].OccurredAt;
        var until = seeded[^3].OccurredAt;
        var expected = seeded.Where(e => e.OccurredAt >= since && e.OccurredAt <= until).Select(e => e.Id).Reverse().ToList();

        var both = await App.QueryAsync(EventsQuery, Tokens.Alice, new { id = device.Id, since, until });
        Assert.False(both.HasErrors, both.ToString());
        Assert.Equal(expected, Ids(both));
        Assert.Contains(seeded[2].Id, Ids(both));    // since is inclusive
        Assert.Contains(seeded[^3].Id, Ids(both));   // until is inclusive
        Assert.Equal(seeded.Count - 4, Ids(both).Count);

        var onlySince = await App.QueryAsync(EventsQuery, Tokens.Alice, new { id = device.Id, since });
        Assert.Equal(seeded.Skip(2).Select(e => e.Id).Reverse(), Ids(onlySince));

        var onlyUntil = await App.QueryAsync(EventsQuery, Tokens.Alice, new { id = device.Id, until });
        Assert.Equal(seeded.SkipLast(2).Select(e => e.Id).Reverse(), Ids(onlyUntil));

        // Same instant written with another offset: the filter compares instants.
        var shifted = await App.QueryAsync(EventsQuery, Tokens.Alice,
            new { id = device.Id, since = since.ToOffset(TimeSpan.FromHours(-7)), until = until.ToOffset(TimeSpan.FromHours(9)) });
        Assert.Equal(expected, Ids(shifted));
    }

    [Fact]
    public async Task Order_is_descending_and_capped()
    {
        foreach (var index in new[] { 0, 42, 6_999 })
        {
            var r = await App.QueryAsync(EventsQuery, Tokens.Alice, new { id = DeviceCatalog.DeviceId(index) });
            Assert.False(r.HasErrors, r.ToString());
            var events = Events(r);
            AssertDescending(events);
            Assert.InRange(events.Count, 3, 20);   // well under the 1 000 cap (the cap itself: StorageTests)
        }
    }

    [Fact]
    public async Task Aliased_lookups_in_one_request_succeed()
    {
        // The gateway alias-batches lookups (d0: deviceById(..) d1: deviceById(..)).
        var fields = string.Concat(Enumerable.Range(0, 20).Select(i =>
            $"d{i}: deviceById(id: \"{DeviceCatalog.DeviceId(i * 350)}\") {{ id installEvents {{ id }} }} "));

        var r = await App.QueryAsync($"{{ {fields} }}", Tokens.Alice);

        Assert.False(r.HasErrors, r.ToString());
        for (var i = 0; i < 20; i++)
        {
            var device = DeviceCatalog.Build(i * 350);   // all TenantA (0..6650)
            var events = r.Data.GetProperty($"d{i}").GetProperty("installEvents");
            Assert.Equal(InstallSeedData.BuildEventsFor(device).Count, events.GetArrayLength());
        }
    }

    [Theory]
    [InlineData("dev-99999")]                  // not a device
    [InlineData("../TenantB/dev-07000")]       // would be normalised into TenantB's prefix without the id check
    [InlineData("dev-00001/../../TenantB/dev-07000")]
    public async Task Unknown_or_non_canonical_id_is_a_stub_with_no_events(string id)
    {
        var r = await App.QueryAsync(EventsQuery, Tokens.Alice, new { id });

        Assert.False(r.HasErrors, r.ToString());
        Assert.Equal(id, r.Data.GetProperty("deviceById").GetProperty("id").GetString());   // always a stub
        Assert.Empty(Events(r));
    }

    [Fact]
    public async Task Unauthenticated_is_AUTH_NOT_AUTHENTICATED()
    {
        var r = await App.QueryAsync(EventsQuery, token: null, new { id = "dev-00001" });

        AssertNotAuthenticated(r);
    }

    [Fact]
    public async Task Wrong_key_token_is_rejected()
    {
        var r = await App.QueryAsync(EventsQuery, Tokens.AliceSignedWithOtherKey, new { id = "dev-00001" });

        AssertNotAuthenticated(r);
    }

    [Fact]
    public async Task Software_catalog_pages_distinct_products_by_name_then_version()
    {
        var index = ExpectedIndex.Value;

        var page = await SoftwareCatalog(Tokens.Alice, search: null, first: 100, offset: 0);
        Assert.Equal(index.Entries.Take(100).Select(e => (e.Name, e.Version, e.Publisher)), page);

        var defaults = await App.QueryAsync("{ software { name version } }", Tokens.Carol);
        Assert.False(defaults.HasErrors, defaults.ToString());
        Assert.Equal(25, defaults.Data.GetProperty("software").GetArrayLength());

        Assert.Equal(index.Entries.Skip(200).Take(10).Select(e => (e.Name, e.Version, e.Publisher)), await SoftwareCatalog(Tokens.Dave, null, first: 10, offset: 200));
        Assert.Equal(100, (await SoftwareCatalog(Tokens.Alice, null, first: 1_000, offset: 0)).Count);
        Assert.Single(await SoftwareCatalog(Tokens.Alice, null, first: 0, offset: 0));

        var docker = await SoftwareCatalog(Tokens.Alice, search: "DOCKER", first: 100, offset: 0);
        Assert.NotEmpty(docker);
        Assert.Equal(index.Search("docker", 100, 0).Select(s => (s.Name, s.Version, s.Publisher)), docker);
        Assert.All(docker, s => Assert.True(s.Name.Contains("Docker", StringComparison.OrdinalIgnoreCase) || s.Publisher.Contains("Docker", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Devices_with_software_pages_the_matching_devices_with_their_events()
    {
        // One product at any version plus another at one exact version: every TenantA device with an event for either,
        // by device id, each with those events newest first (from its own blob). The count is unpaged.
        var index = ExpectedIndex.Value;
        var anyVersion = index.Entries.First(e => e.Name == "Git");
        var oneVersion = index.Entries.First(e => e.Name == "Node.js");
        var keys = new[] { new { name = "Git", version = (string?)null }, new { name = "Node.js", version = (string?)oneVersion.Version } };
        var expected = index.DevicesFor(SeedConstants.TenantA, [new SoftwareKey("Git", null), new SoftwareKey("Node.js", oneVersion.Version)]);
        Assert.InRange(expected.Count, 10, 5_000);
        Assert.NotNull(anyVersion);

        var page = await Find(Tokens.Alice, keys, first: 25, offset: 0);
        Assert.Equal(expected.Count, page.GetProperty("totalCount").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(expected.Take(25), items.Select(i => i.GetProperty("device").GetProperty("id").GetString()));
        foreach (var item in items)
        {
            var id = item.GetProperty("device").GetProperty("id").GetString()!;
            var seeded = InstallSeedData.BuildEventsFor(DeviceCatalog.Build(int.Parse(id[4..], System.Globalization.CultureInfo.InvariantCulture)));
            var want = seeded
                .Where(e => e.Software.Name == "Git" || (e.Software.Name == "Node.js" && e.Software.Version == oneVersion.Version))
                .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id, StringComparer.Ordinal)
                .ToList();
            var events = item.GetProperty("events").EnumerateArray().ToList();
            Assert.NotEmpty(events);
            Assert.Equal(want.Select(e => e.Id), events.Select(e => e.GetProperty("id").GetString()));
            foreach (var (w, g) in want.Zip(events))
            {
                Assert.Equal(id, g.GetProperty("deviceId").GetString());
                Assert.Equal(w.OccurredAt, g.GetProperty("occurredAt").GetDateTimeOffset());
                Assert.Equal(w.Action.ToString().ToUpperInvariant(), g.GetProperty("action").GetString());
                Assert.Equal(w.Software.Version, g.GetProperty("software").GetProperty("version").GetString());
            }
        }

        Assert.Equal(expected.Skip(25).Take(25), DeviceIds(await Find(Tokens.Alice, keys, first: 25, offset: 25)));
        Assert.Equal(expected.TakeLast(2), DeviceIds(await Find(Tokens.Alice, keys, first: 100, offset: expected.Count - 2)));
        Assert.Empty(DeviceIds(await Find(Tokens.Alice, keys, first: 25, offset: expected.Count)));

        // Blank names are dropped, blank versions mean any version, duplicates collapse; clamping as everywhere else.
        var messy = await Find(Tokens.Alice, new object[] { new { name = " ", version = "1" }, new { name = "Git", version = "" }, new { name = "Node.js", version = oneVersion.Version }, keys[0] }, first: 500, offset: -1);
        Assert.Equal(expected.Count, messy.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected.Take(100), DeviceIds(messy));
    }

    [Fact]
    public async Task Matches_give_every_key_its_sorted_device_set_and_device_ids_restrict_the_page()
    {
        var index = ExpectedIndex.Value;
        var version = index.Entries.First(e => e.Name == "Git").Version;
        var keys = new object[] { new { name = "Git", version = (string?)null }, new { name = "Git", version = (string?)version }, new { name = "No Such", version = (string?)null } };

        var r = await App.QueryAsync(MatchesQuery, Tokens.Alice, new { software = keys });

        Assert.False(r.HasErrors, r.ToString());
        var matches = r.Data.GetProperty("devicesWithSoftware").GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(["Git|", $"Git|{version}", "No Such|"], matches.Select(m => $"{m.GetProperty("name").GetString()}|{m.GetProperty("version").GetString()}"));
        var any = matches[0].GetProperty("deviceIds").EnumerateArray().Select(d => d.GetString()!).ToList();
        var one = matches[1].GetProperty("deviceIds").EnumerateArray().Select(d => d.GetString()!).ToList();
        Assert.Equal(index.DevicesFor(SeedConstants.TenantA, [new SoftwareKey("Git", null)]), any);
        Assert.Equal(index.DevicesFor(SeedConstants.TenantA, [new SoftwareKey("Git", version)]), one);
        Assert.True(one.Count > 0 && one.Count < any.Count);
        Assert.Empty(matches[2].GetProperty("deviceIds").EnumerateArray());
        Assert.Equal(any.Count, r.Data.GetProperty("devicesWithSoftware").GetProperty("totalCount").GetInt32());

        // deviceIds restricts the candidates: the one-version set (plus two devices that must be ignored).
        var page = one.Concat(["dev-06999", "dev-11999"]).Take(100).ToArray();
        var details = await Find(Tokens.Alice, new[] { new { name = "Git", version = (string?)null } }, first: 100, offset: 0, deviceIds: page);
        Assert.Equal(one.Take(100), DeviceIds(details));
        Assert.Equal(one.Take(100).Count(), details.GetProperty("totalCount").GetInt32());
        Assert.All(details.GetProperty("items").EnumerateArray(), i => Assert.Contains(i.GetProperty("events").EnumerateArray(), e => e.GetProperty("software").GetProperty("version").GetString() == version));
    }

    [Fact]
    public async Task Devices_with_software_is_scoped_to_the_callers_tenant()
    {
        var index = ExpectedIndex.Value;
        var keys = new[] { new { name = "7-Zip", version = (string?)null } };
        var expectedB = index.DevicesFor(SeedConstants.TenantB, [new SoftwareKey("7-Zip", null)]);

        var dave = await Find(Tokens.Dave, keys, first: 100, offset: 0);
        Assert.Equal(expectedB.Count, dave.GetProperty("totalCount").GetInt32());
        Assert.Equal(expectedB.Take(100), DeviceIds(dave));
        Assert.All(DeviceIds(dave), id => Assert.Equal(SeedConstants.TenantB, DeviceCatalog.TenantOf(int.Parse(id[4..], System.Globalization.CultureInfo.InvariantCulture))));

        var alice = await Find(Tokens.Alice, keys, first: 100, offset: 0);
        Assert.Empty(DeviceIds(alice).Intersect(DeviceIds(dave)));
    }

    [Fact]
    public async Task Devices_with_software_empty_or_unknown_selection_matches_nothing()
    {
        foreach (var keys in new object[][] { [], [new { name = "", version = (string?)null }], [new { name = "No Such Product", version = (string?)null }], [new { name = "Git", version = (string?)"0.0.0" }] })
        {
            var r = await Find(Tokens.Carol, keys, first: 25, offset: 0);
            Assert.Equal(0, r.GetProperty("totalCount").GetInt32());
            Assert.Empty(r.GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public async Task Devices_with_software_rejects_more_than_50_keys()
    {
        var keys = Enumerable.Range(0, 51).Select(i => new { name = $"Product {i}", version = (string?)null }).ToArray();

        var r = await App.QueryAsync(FindQuery, Tokens.Alice, new { software = keys, first = 25, offset = 0 });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("devicesWithSoftware").ValueKind);
        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal(["devicesWithSoftware"], error.GetProperty("path").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("SELECTION_TOO_LARGE", error.GetProperty("extensions").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("bob")]
    [InlineData("erin")]
    public async Task Software_and_devices_with_software_denied_without_the_service(string user)
    {
        var r = await App.QueryAsync(
            "{ software { name } devicesWithSoftware(software: [{ name: \"Git\" }]) { totalCount } deviceById(id: \"dev-00001\") { id } }", Tokens.For(user));

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("software").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("devicesWithSoftware").ValueKind);
        Assert.Equal("dev-00001", r.Data.GetProperty("deviceById").GetProperty("id").GetString());   // sibling unaffected
        var errors = r.Errors.EnumerateArray().ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal("AUTH_NOT_AUTHORIZED", e.GetProperty("extensions").GetProperty("code").GetString()));
        Assert.Equal(["devicesWithSoftware", "software"], errors.Select(e => e.GetProperty("path")[0].GetString()).Order());
    }

    private async Task<JsonElement> Find(string token, object keys, int first, int offset, string[]? deviceIds = null)
    {
        var r = await App.QueryAsync(FindQuery, token, new { software = keys, deviceIds, first, offset });
        Assert.False(r.HasErrors, r.ToString());
        return r.Data.GetProperty("devicesWithSoftware");
    }

    private static List<string> DeviceIds(JsonElement matches) =>
        [.. matches.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("device").GetProperty("id").GetString()!)];

    private async Task<List<(string Name, string Version, string Publisher)>> SoftwareCatalog(string token, string? search, int first, int offset)
    {
        var r = await App.QueryAsync(SoftwareQuery, token, new { search, first, offset });
        Assert.False(r.HasErrors, r.ToString());
        return [.. r.Data.GetProperty("software").EnumerateArray()
            .Select(s => (s.GetProperty("name").GetString()!, s.GetProperty("version").GetString()!, s.GetProperty("publisher").GetString()!))];
    }

    private static void AssertNotAuthenticated(GraphQLResponse r)
    {
        Assert.True(r.HasErrors, r.ToString());
        var error = Assert.Single(r.Errors.EnumerateArray());
        Assert.Equal("AUTH_NOT_AUTHENTICATED", error.GetProperty("extensions").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("deviceById").ValueKind);
    }

    private static List<JsonElement> Events(GraphQLResponse r)
    {
        var events = r.Data.GetProperty("deviceById").GetProperty("installEvents");
        Assert.Equal(JsonValueKind.Array, events.ValueKind);
        return [.. events.EnumerateArray()];
    }

    private static List<string> Ids(GraphQLResponse r) => [.. Events(r).Select(e => e.GetProperty("id").GetString()!)];

    private static void AssertDescending(List<JsonElement> events)
    {
        var times = events.Select(e => e.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        Assert.Equal(times.OrderDescending(), times);
    }

    /// <summary>Every field of every event equals the seed function's output, newest first.</summary>
    private static void AssertEqualsSeed(SeedDevice device, List<JsonElement> events)
    {
        var expected = InstallSeedData.BuildEventsFor(device).Reverse().ToList();
        Assert.Equal(expected.Count, events.Count);
        foreach (var (e, actual) in expected.Zip(events))
        {
            Assert.Equal(e.Id, actual.GetProperty("id").GetString());
            Assert.Equal(device.Id, actual.GetProperty("deviceId").GetString());
            Assert.Equal(e.OccurredAt, actual.GetProperty("occurredAt").GetDateTimeOffset());
            Assert.Equal(e.Action.ToString().ToUpperInvariant(), actual.GetProperty("action").GetString());
            Assert.Equal(e.Result.ToString().ToUpperInvariant(), actual.GetProperty("result").GetString());
            var software = actual.GetProperty("software");
            Assert.Equal(e.Software.Name, software.GetProperty("name").GetString());
            Assert.Equal(e.Software.Version, software.GetProperty("version").GetString());
            Assert.Equal(e.Software.Publisher, software.GetProperty("publisher").GetString());
        }
    }
}
