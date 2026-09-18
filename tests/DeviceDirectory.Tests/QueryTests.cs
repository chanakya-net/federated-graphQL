using System.Net;
using System.Text.Json;
using SoR.DeviceDirectory.Tests.Support;
using SoR.Shared.Seeding;

namespace SoR.DeviceDirectory.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class QueryTests(PostgresFixture pg)
{
    private const string LookupQuery =
        "query($id: ID!) { device(id: $id) { id hostname os ipAddress lastSeenAt tenantId } }";

    private const string SearchQuery =
        "query($s: String, $first: Int!, $offset: Int!) { devices(search: $s, first: $first, offset: $offset) { totalCount items { id tenantId hostname os } } }";

    [Fact]
    public async Task Device_lookup_returns_own_tenant()
    {
        var app = await pg.SeededAppAsync();
        var expected = DeviceCatalog.Build(1);

        var r = await app.QueryAsync(LookupQuery, Tokens.Alice, new { id = "dev-00001" });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(r.HasErrors, r.ToString());
        var d = r.Data.GetProperty("device");
        Assert.Equal("dev-00001", d.GetProperty("id").GetString());
        Assert.Equal(expected.Hostname, d.GetProperty("hostname").GetString());
        Assert.Equal(expected.Os, d.GetProperty("os").GetString());
        Assert.Equal(expected.IpAddress, d.GetProperty("ipAddress").GetString());
        Assert.Equal(expected.LastSeenAt, d.GetProperty("lastSeenAt").GetDateTimeOffset());
        Assert.Equal(SeedConstants.TenantA, d.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task Device_lookup_other_tenant_is_null_without_error()
    {
        var app = await pg.SeededAppAsync();

        var r = await app.QueryAsync(LookupQuery, Tokens.Dave, new { id = "dev-00001" });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(r.HasErrors, r.ToString());
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("device").ValueKind);
    }

    [Fact]
    public async Task Device_lookup_own_tenant_B_device_works_for_dave()
    {
        var app = await pg.SeededAppAsync();

        var r = await app.QueryAsync(LookupQuery, Tokens.Dave, new { id = "dev-11999" });

        Assert.False(r.HasErrors, r.ToString());
        Assert.Equal(DeviceCatalog.Build(11_999).Hostname, r.Data.GetProperty("device").GetProperty("hostname").GetString());
        Assert.Equal(SeedConstants.TenantB, r.Data.GetProperty("device").GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task Device_lookup_unknown_id_is_null()
    {
        var app = await pg.SeededAppAsync();

        var r = await app.QueryAsync(LookupQuery, Tokens.Alice, new { id = "dev-99999" });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(r.HasErrors, r.ToString());
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("device").ValueKind);
    }

    [Fact]
    public async Task Service_access_is_not_required()
    {
        // Device Directory checks authentication only; carol has softwareinstall alone and still sees her tenant.
        var app = await pg.SeededAppAsync();

        var r = await app.QueryAsync(LookupQuery, Tokens.Carol, new { id = "dev-00001" });

        Assert.False(r.HasErrors, r.ToString());
        Assert.Equal("dev-00001", r.Data.GetProperty("device").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Aliased_lookups_and_search_in_one_request_succeed()
    {
        // The gateway alias-batches lookups; root fields resolve in parallel and must not share a DbContext.
        var app = await pg.SeededAppAsync();
        var query = string.Concat(Enumerable.Range(0, 20).Select(i => $"d{i}: device(id: \"{DeviceCatalog.DeviceId(i)}\") {{ id }} "));

        var r = await app.QueryAsync($"{{ {query} list: devices(first: 5) {{ totalCount }} }}", Tokens.Alice);

        Assert.False(r.HasErrors, r.ToString());
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(DeviceCatalog.DeviceId(i), r.Data.GetProperty($"d{i}").GetProperty("id").GetString());
        }

        Assert.Equal(SeedConstants.TenantADeviceCount, r.Data.GetProperty("list").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Devices_search_scoped_to_tenant()
    {
        var app = await pg.SeededAppAsync();

        // "dev-00" matches ids dev-00000..dev-00999: all TenantA. Dave must see none of them.
        var dave = await Search(app, Tokens.Dave, "dev-00", first: 100);
        Assert.All(Items(dave), i => Assert.Equal(SeedConstants.TenantB, i.GetProperty("tenantId").GetString()));
        Assert.Equal(ExpectedCount(SeedConstants.TenantB, "dev-00"), TotalCount(dave));
        Assert.True(TotalCount(dave) <= 5_000);

        var alice = await Search(app, Tokens.Alice, "dev-00", first: 100);
        Assert.Equal(ExpectedCount(SeedConstants.TenantA, "dev-00"), TotalCount(alice));
        Assert.All(Items(alice), i => Assert.Equal(SeedConstants.TenantA, i.GetProperty("tenantId").GetString()));

        // No search: the whole tenant.
        var all = await Search(app, Tokens.Dave, null, first: 100);
        Assert.Equal(5_000, TotalCount(all));
        Assert.All(Items(all), i => Assert.Equal(SeedConstants.TenantB, i.GetProperty("tenantId").GetString()));
    }

    [Fact]
    public async Task Devices_search_matches_hostname_and_os_case_insensitively()
    {
        var app = await pg.SeededAppAsync();
        var known = DeviceCatalog.Build(42);

        // Hostname: an uppercased fragment of a known hostname returns that device.
        var fragment = known.Hostname[..Math.Min(known.Hostname.Length, 12)].ToUpperInvariant();
        var byHost = await Search(app, Tokens.Alice, fragment, first: 100);
        Assert.Contains(Items(byHost), i => i.GetProperty("id").GetString() == known.Id);
        Assert.Equal(ExpectedCount(SeedConstants.TenantA, fragment), TotalCount(byHost));

        // Full hostname in mixed case (id and os cannot contain it): exactly the devices with that hostname.
        var byFullHost = await Search(app, Tokens.Alice, known.Hostname.ToUpperInvariant(), first: 100);
        Assert.Contains(Items(byFullHost), i => i.GetProperty("id").GetString() == known.Id);

        // OS, case-insensitive.
        var byOs = await Search(app, Tokens.Alice, "uBuNtU", first: 100);
        Assert.Equal(ExpectedCount(SeedConstants.TenantA, "ubuntu"), TotalCount(byOs));
        Assert.True(TotalCount(byOs) > 0);
        Assert.All(Items(byOs), i => Assert.Contains("ubuntu", i.GetProperty("os").GetString()!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Devices_search_treats_like_wildcards_literally()
    {
        var app = await pg.SeededAppAsync();

        Assert.Equal(0, TotalCount(await Search(app, Tokens.Alice, "%", first: 10)));
        Assert.Equal(0, TotalCount(await Search(app, Tokens.Alice, "dev_0", first: 10)));
    }

    [Fact]
    public async Task Devices_first_is_clamped()
    {
        var app = await pg.SeededAppAsync();

        var big = await Search(app, Tokens.Alice, null, first: 500);
        Assert.Equal(100, Items(big).Count);
        Assert.Equal(SeedConstants.TenantADeviceCount, TotalCount(big));

        var zero = await Search(app, Tokens.Alice, null, first: 0);
        Assert.Single(Items(zero));

        var negativeOffset = await Search(app, Tokens.Alice, null, first: 3, offset: -10);
        var firstPage = await Search(app, Tokens.Alice, null, first: 3, offset: 0);
        Assert.Equal(Ids(firstPage), Ids(negativeOffset));
    }

    [Fact]
    public async Task Devices_defaults_are_25_and_0_and_pages_are_stable()
    {
        var app = await pg.SeededAppAsync();

        var r = await app.QueryAsync("{ devices { totalCount items { id } } }", Tokens.Dave);
        Assert.False(r.HasErrors, r.ToString());
        Assert.Equal(25, r.Data.GetProperty("devices").GetProperty("items").GetArrayLength());

        var p1 = await Search(app, Tokens.Dave, null, first: 50, offset: 0);
        var p2 = await Search(app, Tokens.Dave, null, first: 50, offset: 50);
        var both = await Search(app, Tokens.Dave, null, first: 100, offset: 0);
        Assert.Empty(Ids(p1).Intersect(Ids(p2)));
        Assert.Equal(Ids(both), Ids(p1).Concat(Ids(p2)).ToList());
    }

    [Fact]
    public async Task Unauthenticated_yields_AUTH_NOT_AUTHENTICATED()
    {
        var app = await pg.SeededAppAsync();

        var r = await app.QueryAsync(LookupQuery, token: null, new { id = "dev-00001" });

        AssertNotAuthenticated(r);
    }

    [Fact]
    public async Task Wrong_key_token_is_rejected()
    {
        var app = await pg.SeededAppAsync();

        var r = await app.QueryAsync(LookupQuery, Tokens.AliceSignedWithOtherKey, new { id = "dev-00001" });

        AssertNotAuthenticated(r);
    }

    private static void AssertNotAuthenticated(GraphQLResponse r)
    {
        Assert.True(r.HasErrors, r.ToString());
        Assert.Equal("AUTH_NOT_AUTHENTICATED", r.Errors[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("device").ValueKind);
    }

    private static async Task<JsonElement> Search(DeviceDirectoryApp app, string token, string? search, int first, int offset = 0)
    {
        var r = await app.QueryAsync(SearchQuery, token, new { s = search, first, offset });
        Assert.False(r.HasErrors, r.ToString());
        return r.Data.GetProperty("devices");
    }

    private static int TotalCount(JsonElement devices) => devices.GetProperty("totalCount").GetInt32();

    private static List<JsonElement> Items(JsonElement devices) => [.. devices.GetProperty("items").EnumerateArray()];

    private static List<string> Ids(JsonElement devices) => [.. Items(devices).Select(i => i.GetProperty("id").GetString()!)];

    /// <summary>Contract semantics computed from the catalog: id, hostname or os contains the text, ignoring case.</summary>
    private static int ExpectedCount(string tenantId, string search) =>
        DeviceCatalog.ForTenant(tenantId).Count(d =>
            d.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || d.Hostname.Contains(search, StringComparison.OrdinalIgnoreCase)
            || d.Os.Contains(search, StringComparison.OrdinalIgnoreCase));
}
