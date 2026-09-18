using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SoR.DeviceDirectory.Data;

namespace SoR.DeviceDirectory.Tests;

/// <summary>Design-time checks, no database: the committed migrations match the model and stay in the owned schema.</summary>
public sealed class MigrationTests
{
    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = new DeviceDbContextFactory().CreateDbContext([]);

        Assert.False(db.Database.HasPendingModelChanges(), "model changed: add a migration (Data/Migrations)");
        Assert.NotEmpty(db.Database.GetMigrations());
    }

    [Fact]
    public void Migration_script_touches_only_the_owned_schema()
    {
        // devdir_user owns device_directory and has no rights on public (infra/postgres/init).
        using var db = new DeviceDbContextFactory().CreateDbContext([]);

        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.Contains("device_directory.__ef_migrations_history", script, StringComparison.Ordinal);
        Assert.Contains("device_directory.devices", script, StringComparison.Ordinal);
        Assert.DoesNotContain("public.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__EFMigrationsHistory", script, StringComparison.Ordinal);
    }
}
