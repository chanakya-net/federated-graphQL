using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using SoR.DeviceDirectory.Tests.Support;
using SoR.Shared.Seeding;
using Xunit.Abstractions;

namespace SoR.DeviceDirectory.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SeedingTests(PostgresFixture pg, ITestOutputHelper output)
{
    [Fact]
    public async Task Seed_inserts_12000_and_is_idempotent()
    {
        var db = await pg.CreateFreshDatabaseAsync();
        DateTimeOffset completedAt;

        await using (var first = new DeviceDirectoryApp(pg.AppConnectionString(db), Tokens.SigningKey))
        {
            await first.WaitUntilHealthyAsync();
            Assert.Equal(12_000L, await pg.AdminScalarAsync<long>(db, "select count(*) from device_directory.devices"));
            Assert.Equal(1L, await pg.AdminScalarAsync<long>(db, "select count(*) from device_directory.seed_state"));
            Assert.Equal(12_000, await pg.AdminScalarAsync<int>(db, "select row_count from device_directory.seed_state where key = 'devices'"));
            completedAt = new DateTimeOffset(
                await pg.AdminScalarAsync<DateTime>(db, "select completed_at from device_directory.seed_state"), TimeSpan.Zero);
            output.WriteLine(Assert.Single(first.Logs.Messages, m => m.Contains("seed completed: 12000 devices", StringComparison.Ordinal)));
        }

        // Restart against the same database: the marker is found, nothing is reseeded.
        await using var second = new DeviceDirectoryApp(pg.AppConnectionString(db), Tokens.SigningKey);
        await second.WaitUntilHealthyAsync();

        Assert.Equal(12_000L, await pg.AdminScalarAsync<long>(db, "select count(*) from device_directory.devices"));
        Assert.Equal(1L, await pg.AdminScalarAsync<long>(db, "select count(*) from device_directory.seed_state"));
        var completedAgain = await pg.AdminScalarAsync<DateTime>(db, "select completed_at from device_directory.seed_state");
        Assert.Equal(completedAt, new DateTimeOffset(completedAgain, TimeSpan.Zero));
        output.WriteLine(Assert.Single(second.Logs.Messages, m => m.Contains("seed already present", StringComparison.Ordinal)));
        Assert.DoesNotContain(second.Logs.Messages, m => m.Contains("seeded ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_seed_without_marker_is_discarded_and_redone()
    {
        var db = await pg.CreateFreshDatabaseAsync();
        await using (var first = new DeviceDirectoryApp(pg.AppConnectionString(db), Tokens.SigningKey))
        {
            await first.WaitUntilHealthyAsync();
        }

        // Simulate a crash mid-seed: marker missing, rows partly there, plus a row no seed would write.
        await pg.AdminExecuteAsync(db, """
            delete from device_directory.seed_state;
            delete from device_directory.devices where id >= 'dev-05000';
            insert into device_directory.devices (id, tenant_id, hostname, os, ip_address, last_seen_at)
              values ('dev-stray', 'TenantA', 'stray', 'none', '0.0.0.0', '2000-01-01T00:00:00Z');
            """);

        await using var second = new DeviceDirectoryApp(pg.AppConnectionString(db), Tokens.SigningKey);
        await second.WaitUntilHealthyAsync();

        Assert.Equal(12_000L, await pg.AdminScalarAsync<long>(db, "select count(*) from device_directory.devices"));
        Assert.Equal(0L, await pg.AdminScalarAsync<long>(db, "select count(*) from device_directory.devices where id = 'dev-stray'"));
        Assert.Equal(1L, await pg.AdminScalarAsync<long>(db, "select count(*) from device_directory.seed_state"));
    }

    [Fact]
    public async Task Health_is_unhealthy_until_seed_completes()
    {
        var db = await pg.CreateFreshDatabaseAsync();
        await using var app = new DeviceDirectoryApp(pg.AppConnectionString(db), Tokens.SigningKey);

        var observed = await app.WaitUntilHealthyAsync();
        output.WriteLine($"/health observed: {string.Join(", ", observed.GroupBy(s => s).Select(g => $"{(int)g.Key} x{g.Count()}"))}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, observed[0]);
        Assert.Equal(HttpStatusCode.OK, observed[^1]);
        Assert.Equal(12_000L, await pg.AdminScalarAsync<long>(db, "select count(*) from device_directory.devices"));
    }

    [Fact]
    public async Task Health_is_unhealthy_without_signing_key()
    {
        await pg.SeededAppAsync();   // data is seeded; only the key is missing
        await using var app = new DeviceDirectoryApp(pg.AppConnectionString(), signingKey: null);
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
        Assert.Equal(HealthStatus.Healthy, report.Entries["postgres"].Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["auth-config"].Status);

        using var client = app.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Seed_split_is_7000_5000()
    {
        await pg.SeededAppAsync();

        Assert.Equal(7_000L, await pg.AdminScalarAsync<long>("sor", "select count(*) from device_directory.devices where tenant_id = 'TenantA'"));
        Assert.Equal(5_000L, await pg.AdminScalarAsync<long>("sor", "select count(*) from device_directory.devices where tenant_id = 'TenantB'"));
        Assert.Equal(12_000L, await pg.AdminScalarAsync<long>("sor", "select count(*) from device_directory.devices"));
    }

    [Fact]
    public async Task Seeded_rows_equal_the_catalog()
    {
        // Determinism: every stored value, LastSeenAt included, is exactly DeviceCatalog.Build(i).
        await pg.SeededAppAsync();
        var expected = DeviceCatalog.All().ToDictionary(d => d.Id);

        await using var conn = await pg.OpenAdminAsync();
        await using var cmd = new NpgsqlCommand(
            "select id, tenant_id, hostname, os, ip_address, last_seen_at from device_directory.devices", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var seen = 0;
        while (await reader.ReadAsync())
        {
            var d = expected[reader.GetString(0)];
            Assert.Equal(d.TenantId, reader.GetString(1));
            Assert.Equal(d.Hostname, reader.GetString(2));
            Assert.Equal(d.Os, reader.GetString(3));
            Assert.Equal(d.IpAddress, reader.GetString(4));
            Assert.Equal(d.LastSeenAt, reader.GetFieldValue<DateTimeOffset>(5));
            seen++;
        }

        Assert.Equal(SeedConstants.TotalDevices, seen);
    }

    [Fact]
    public async Task Tables_live_only_in_the_owned_schema()
    {
        await pg.SeededAppAsync();

        var tables = new List<string>();
        await using var conn = await pg.OpenAdminAsync();
        await using var cmd = new NpgsqlCommand(
            "select table_schema || '.' || table_name from information_schema.tables where table_schema not in ('pg_catalog', 'information_schema') order by 1", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        Assert.Equal(
            ["device_directory.__ef_migrations_history", "device_directory.devices", "device_directory.seed_state"],
            tables);
        Assert.Equal("devdir_user", await pg.AdminScalarAsync<string>("sor",
            "select tableowner from pg_tables where schemaname = 'device_directory' and tablename = 'devices'"));
    }
}
