using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SoR.DeviceDirectory.Data;

/// <summary>
/// Design-time factory: lets <c>dotnet ef migrations add</c> build the model without a database or the app host.
/// The connection string is never opened.
/// </summary>
public sealed class DeviceDbContextFactory : IDesignTimeDbContextFactory<DeviceDbContext>
{
    public DeviceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DeviceDbContext>();
        DeviceDbContext.ConfigureNpgsql(options, "Host=localhost;Database=design;Username=x;Password=x");
        return new DeviceDbContext(options.Options);
    }
}
