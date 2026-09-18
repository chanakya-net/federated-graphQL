using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace SoR.DeviceDirectory.Data;

public sealed class DeviceDbContext(DbContextOptions<DeviceDbContext> options) : DbContext(options)
{
    /// <summary>The only schema the <c>devdir_user</c> role owns (infra/postgres/init). It has no rights on <c>public</c>.</summary>
    public const string Schema = "device_directory";

    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>Used only when no connection string is configured (schema export, design time). Never used in compose.</summary>
    public const string FallbackConnectionString =
        "Host=localhost;Database=sor;Username=devdir_user;Password=devdir_pw;Search Path=device_directory";

    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();

    public DbSet<SeedStateEntity> SeedState => Set<SeedStateEntity>();

    /// <summary>Single place for the provider options, shared by DI and the design-time factory.</summary>
    public static void ConfigureNpgsql(DbContextOptionsBuilder options, string connectionString)
    {
        // Npgsql prefers GSS encryption by default and probes libgssapi_krb5, which the alpine runtime image
        // lacks ("Error loading shared library libgssapi_krb5.so.2" on every start). No Kerberos in this POC.
        var cs = new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable };
        options.UseNpgsql(cs.ConnectionString, ConfigureNpgsql);
    }

    private static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npg) =>
        npg.MigrationsHistoryTable(MigrationsHistoryTable, Schema);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<DeviceEntity>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasMaxLength(16);
            e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(32);
            e.Property(x => x.Hostname).HasColumnName("hostname").HasMaxLength(128);
            e.Property(x => x.Os).HasColumnName("os").HasMaxLength(64);
            e.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.HasIndex(x => new { x.TenantId, x.Hostname });
            e.HasIndex(x => new { x.TenantId, x.Os });
        });
        b.Entity<SeedStateEntity>(e =>
        {
            e.ToTable("seed_state");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.RowCount).HasColumnName("row_count");
        });
    }
}
