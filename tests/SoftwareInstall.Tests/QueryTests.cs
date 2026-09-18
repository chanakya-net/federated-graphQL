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
