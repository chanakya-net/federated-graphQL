using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SoR.Patch.Data;
using SoR.Patch.GraphQL;
using SoR.Patch.Seeding;
using SoR.Shared.Auth;

var builder = WebApplication.CreateBuilder(args);

// MongoClient construction never connects, so `schema export` works with no MongoDB and no configuration.
// Seeding runs in SeedHostedService after startup; nothing here touches the database.
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection(MongoOptions.Section));
builder.Services.AddSingleton<IMongoClient>(sp => new MongoClient(
    sp.GetRequiredService<IOptions<MongoOptions>>().Value.ToClientSettings()));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>()
    .GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));
builder.Services.AddSingleton<IPatchStore, PatchStore>();

builder.Services.AddDevJwtAuthentication(builder.Configuration);   // also adds the "auth-config" health check
builder.Services.AddServiceAccessPolicy(DevAuth.Services.Patch);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SeedState>();
builder.Services.AddHostedService<SeedHostedService>();
builder.Services.AddHealthChecks()
    .AddCheck<MongoPingHealthCheck>("mongo", timeout: MongoPingHealthCheck.Timeout)
    .AddCheck<SeedCompletedHealthCheck>("seed");   // Unhealthy until the seed marker exists

builder
    .AddGraphQL(PatchSchema.Name)
    .AddPatchTypes()
    .ModifyRequestOptions(o => o.IncludeExceptionDetails = builder.Environment.IsDevelopment());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGraphQL();
await app.RunWithGraphQLCommandsAsync(args);   // enables `dotnet run -- schema export --output <file>`
