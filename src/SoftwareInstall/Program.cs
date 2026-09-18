using Microsoft.Extensions.Options;
using SoR.Shared.Auth;
using SoR.SoftwareInstall.GraphQL;
using SoR.SoftwareInstall.Seeding;
using SoR.SoftwareInstall.Storage;

var builder = WebApplication.CreateBuilder(args);

// Constructing the client only parses the connection string; nothing connects before the first blob operation
// (SeedHostedService, after startup), so `schema export` needs no storage and no Blob__* settings.
builder.Services.Configure<BlobOptions>(builder.Configuration.GetSection(BlobOptions.Section));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<BlobOptions>>().Value.CreateContainerClient());
builder.Services.AddSingleton<InstallEventsBlobStore>();
builder.Services.AddSingleton<IInstallEventsStore>(sp => sp.GetRequiredService<InstallEventsBlobStore>());

builder.Services.AddDevJwtAuthentication(builder.Configuration);   // also adds the "auth-config" health check
builder.Services.AddServiceAccessPolicy(DevAuth.Services.SoftwareInstall);

builder.Services.AddSingleton<SeedState>();
builder.Services.AddHostedService<SeedHostedService>();
builder.Services.AddHealthChecks()
    .AddCheck<BlobContainerHealthCheck>("blob")
    .AddCheck<SeedCompletedHealthCheck>("seed");   // Unhealthy until the seed marker exists

builder
    .AddGraphQL(SoftwareInstallSchema.Name)
    .AddSoftwareInstallTypes()
    .ModifyRequestOptions(o => o.IncludeExceptionDetails = builder.Environment.IsDevelopment());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGraphQL();
await app.RunWithGraphQLCommandsAsync(args);   // enables `dotnet run -- schema export --output <file>`
