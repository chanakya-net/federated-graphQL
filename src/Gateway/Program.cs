using HotChocolate.Language;
using SoR.Gateway;
using SoR.Gateway.Timeline;
using SoR.Gateway.Transport;
using SoR.Shared.Auth;

if (args is ["timeline-catalog", "generate", ..])
{
    var options = args.Skip(2).Chunk(2).ToDictionary(pair => pair[0], pair => pair.Length == 2 ? pair[1] : "", StringComparer.Ordinal);
    static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"Missing {name}. Usage: timeline-catalog generate --archive PATH --output PATH --source-root PATH --schemas PATH");
    var generated = TimelineCatalogGenerator.GenerateFromDirectories(
        Required(options, "--archive"),
        Required(options, "--output"),
        Required(options, "--source-root"),
        Required(options, "--schemas"));
    Console.WriteLine($"generated {generated.Sources.Count} timeline sources paired with {generated.SchemaHash}");
    return;
}

if (args is ["timeline-catalog", "source-projects", "--settings", var settingsDirectory])
{
    foreach (var source in TimelineCatalogGenerator.SourceProjects(Path.GetFullPath(settingsDirectory)))
        Console.WriteLine($"{source.SchemaBaseName}\t{source.ProjectName}");
    return;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Per-subgraph HttpClient timeout: a stopped subgraph fails fast (DNS / connection refused), a hung one only
// through this. It surfaces as a field error, the rest of the request carries on (docs/version-facts.md §6).
var timeoutSeconds = config.GetValue(GatewaySettings.TimeoutVariable, GatewaySettings.DefaultTimeoutSeconds);
if (timeoutSeconds <= 0)
{
    throw new InvalidOperationException($"{GatewaySettings.TimeoutVariable} must be a positive number of seconds, got {timeoutSeconds}.");
}

var timeout = TimeSpan.FromSeconds(timeoutSeconds);
var archive = Path.GetFullPath(config[GatewaySettings.ArchiveVariable] ?? Path.Combine(AppContext.BaseDirectory, "gateway.far"));
var archiveSnapshot = await GatewayArchive.LoadSnapshotAsync(archive);   // immutable paired snapshot; no live FAR reload
var catalogPath = Path.GetFullPath(config[GatewaySettings.CatalogVariable] ?? Path.Combine(Path.GetDirectoryName(archive)!, "timeline-sources.json"));
var timelineCatalog = TimelineSourceCatalogLoader.Load(catalogPath, archiveSnapshot.SchemaHash, archive, archiveSnapshot.Schema);

builder.Services.AddDevJwtAuthentication(config);   // signature + expiry only; also the "auth-config" health check
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<ForwardAuthorizationHandler>();
builder.Services.AddSingleton(timelineCatalog);
builder.Services.AddHealthChecks();   // healthy without any subgraph: the gateway serves from the archive

var gateway = builder
    .AddGraphQLGateway()
    .AddInMemoryConfiguration(archiveSnapshot.Schema, archiveSnapshot.Settings)
    // HC 16 default security turns introspection off outside Development; Nitro and the schema checks need it.
    .DisableIntrospection(false)
    .ModifyRequestOptions(o =>
    {
        // Query plan view in Nitro: it sends `Fusion-Operation-Plan: 1`, and the gateway only answers with
        // `extensions.fusion.operationPlan` when plan requests are allowed (off by default in 16.6.6).
        o.CollectOperationPlanTelemetry = true;
        o.AllowOperationPlanRequests = true;
        // Standard null propagation; the extension fields are nullable, so a failure stays at the field (plan §7).
        o.DefaultErrorHandlingMode = ErrorHandlingMode.Propagate;
        o.AllowErrorHandlingModeOverride = false;
    });

// One generated client registration per FAR source schema: the HttpClient name is the source-schema name, so the
// configured timeout and header forwarding apply, and its URL replaces the one baked into the archive. Startup
// rejects every source without a URL so none can fall through to Fusion's anonymous default transport.
var registrations = SubgraphClientRegistration.Add(builder.Services, config, archiveSnapshot.SourceSchemaNames, timeout);
foreach (var registration in registrations)
{
    gateway.AddHttpClientConfiguration(registration.Name, registration.Url);
}

var app = builder.Build();
app.UseAuthentication();
app.UseMiddleware<EdgeAuthMiddleware>();
app.MapHealthChecks("/health");
app.MapGet("/timeline-sources", (HttpContext context, TimelineSourceCatalogSnapshot snapshot) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Bytes(snapshot.Json, "application/json; charset=utf-8");
});
app.MapGraphQL();   // POST /graphql; GET /graphql/ serves the embedded Nitro UI (GET /graphql -> 301)
await app.RunAsync();
