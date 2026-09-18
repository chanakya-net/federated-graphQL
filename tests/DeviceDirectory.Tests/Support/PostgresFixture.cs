using DotNet.Testcontainers.Configurations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace SoR.DeviceDirectory.Tests.Support;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

/// <summary>
/// One <c>postgres:17-alpine</c> per test run, initialised by P2C's init script (roles + owned schemas), exactly
/// like compose. Database <c>sor</c> is what the init script prepared; it backs one shared, seeded app.
/// Tests that need an unseeded database get a clone of the pristine post-init state (<see cref="CreateFreshDatabaseAsync"/>).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:17-alpine";
    private const string Database = "sor";
    private const string Template = "devdir_pristine";
    private const string AppUser = "devdir_user";

    private readonly string _appPassword = Repo.Env("DEVDIR_DB_PASSWORD");
    private readonly PostgreSqlContainer _container;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DeviceDirectoryApp? _seededApp;
    private int _databases;

    public PostgresFixture()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase(Database)
            .WithUsername("postgres")
            .WithPassword(Repo.Env("POSTGRES_PASSWORD"))
            .WithEnvironment("DEVDIR_DB_PASSWORD", _appPassword)
            .WithEnvironment("VULN_DB_PASSWORD", Repo.Env("VULN_DB_PASSWORD"))
            .WithResourceMapping(InitScript(), "/docker-entrypoint-initdb.d/01-roles-and-schemas.sh",
                fileMode: UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute
                        | UnixFileModes.GroupRead | UnixFileModes.GroupExecute
                        | UnixFileModes.OtherRead | UnixFileModes.OtherExecute)
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        // Snapshot the pristine post-init state before anything connects to "sor" (CREATE DATABASE ... TEMPLATE
        // needs the source to be idle). Clones keep the schema ownership and the REVOKE on public.
        await AdminExecuteAsync("postgres", $"CREATE DATABASE {Template} TEMPLATE {Database}");
    }

    public async Task DisposeAsync()
    {
        if (_seededApp is not null) await _seededApp.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    /// <summary>Connection string as compose passes it: <c>devdir_user</c>, <c>Search Path=device_directory</c>.</summary>
    public string AppConnectionString(string database = Database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = AppUser,
            Password = _appPassword,
            SearchPath = "device_directory",
        }.ConnectionString;

    /// <summary>A new database in the state the init script leaves <c>sor</c> in (roles and empty owned schema).</summary>
    public async Task<string> CreateFreshDatabaseAsync()
    {
        var name = $"devdir_fresh_{Interlocked.Increment(ref _databases)}";
        await AdminExecuteAsync("postgres", $"CREATE DATABASE {name} TEMPLATE {Template}");
        return name;
    }

    /// <summary>The shared app on <c>sor</c>, started once and seeded (/health 200).</summary>
    public async Task<DeviceDirectoryApp> SeededAppAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_seededApp is null)
            {
                var app = new DeviceDirectoryApp(AppConnectionString(), Tokens.SigningKey);
                await app.WaitUntilHealthyAsync();
                _seededApp = app;
            }

            return _seededApp;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AdminExecuteAsync(string database, string sql)
    {
        await using var conn = await OpenAdminAsync(database);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> AdminScalarAsync<T>(string database, string sql)
    {
        await using var conn = await OpenAdminAsync(database);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<NpgsqlConnection> OpenAdminAsync(string database = Database)
    {
        var cs = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database, Pooling = false };
        var conn = new NpgsqlConnection(cs.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static byte[] InitScript()
    {
        using var stream = typeof(PostgresFixture).Assembly.GetManifestResourceStream("01-roles-and-schemas.sh")
            ?? throw new InvalidOperationException("embedded init script missing");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
