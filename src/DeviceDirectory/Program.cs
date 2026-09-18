using Microsoft.EntityFrameworkCore;
using SoR.DeviceDirectory.Data;
using SoR.DeviceDirectory.GraphQL;
using SoR.DeviceDirectory.Seeding;
using SoR.Shared.Auth;

var builder = WebApplication.CreateBuilder(args);

// The fallback exists only so `schema export` and design-time tools never throw on a missing value.
// Nothing here opens a connection; seeding (and migration) runs in SeedHostedService after startup.
var connectionString = builder.Configuration.GetConnectionString("DeviceDirectory") ?? DeviceDbContext.FallbackConnectionString;
builder.Services.AddDbContextPool<DeviceDbContext>(o => DeviceDbContext.ConfigureNpgsql(o, connectionString));

builder.Services.AddDevJwtAuthentication(builder.Configuration);   // also adds the "auth-config" health check
builder.Services.AddAuthorization();

builder.Services.AddSingleton<SeedState>();
builder.Services.AddHostedService<SeedHostedService>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<DeviceDbContext>("postgres")
    .AddCheck<SeedCompletedHealthCheck>("seed");   // Unhealthy until the seed marker exists

builder
    .AddGraphQL(DeviceDirectorySchema.Name)
    .AddDeviceDirectoryTypes()
    .ModifyRequestOptions(o => o.IncludeExceptionDetails = builder.Environment.IsDevelopment());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGraphQL();
await app.RunWithGraphQLCommandsAsync(args);   // enables `dotnet run -- schema export --output <file>`
