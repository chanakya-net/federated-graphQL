using HotChocolate.Language;
using SoR.Gateway;
using SoR.Gateway.Transport;
using SoR.Shared.Auth;

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
var searchTimeoutSeconds = config.GetValue(GatewaySettings.SearchTimeoutVariable, GatewaySettings.DefaultSearchTimeoutSeconds);
if (searchTimeoutSeconds <= 0)
    throw new InvalidOperationException($"{GatewaySettings.SearchTimeoutVariable} must be a positive number of seconds.");
var archive = Path.GetFullPath(config[GatewaySettings.ArchiveVariable] ?? Path.Combine(AppContext.BaseDirectory, "gateway.far"));
await GatewayArchive.EnsureUsableAsync(archive);   // Fusion itself would wait forever on a missing or corrupt archive

builder.Services.AddDevJwtAuthentication(config);   // signature + expiry only; also the "auth-config" health check
builder.Services.AddTransient<ForwardAuthorizationHandler>();
builder.Services.AddHealthChecks();   // healthy without any subgraph: the gateway serves from the archive

var gateway = builder
    .AddGraphQLGateway()
    .AddFileSystemConfiguration(archive)
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

// One code-level client configuration per source schema: its HttpClient name is the source-schema name, so the
// timeout and header forwarding apply, and its URL (from env) replaces the one baked into the archive. There is
// deliberately no "fusion" client: a source schema left to it would call out anonymously with a 100 s timeout.
var unconfigured = new List<string>();
foreach (var name in SubgraphClientNames.All)
{
    var variable = SubgraphClientNames.UrlVariable(name);
    var url = config[variable];
    if (string.IsNullOrWhiteSpace(url))
    {
        unconfigured.Add(variable);
        url = $"http://localhost:0/unconfigured/{name}";   // refused at once: that subgraph's fields fail, startup does not
    }

    builder.Services
        .AddHttpClient(name, c => c.Timeout = name == SubgraphClientNames.DeviceSearch
            ? TimeSpan.FromSeconds(searchTimeoutSeconds) : timeout)
        .AddHttpMessageHandler<ForwardAuthorizationHandler>();
    gateway.AddHttpClientConfiguration(name, new Uri(url));
}

var app = builder.Build();
if (unconfigured.Count > 0)
{
    app.Logger.LogWarning("Not set: {Variables}. Those subgraphs are unreachable.", string.Join(", ", unconfigured));
}

app.UseAuthentication();
app.UseMiddleware<EdgeAuthMiddleware>();
app.MapHealthChecks("/health");
app.MapGraphQL();   // POST /graphql; GET /graphql/ serves the embedded Nitro UI (GET /graphql -> 301)
await app.RunAsync();
