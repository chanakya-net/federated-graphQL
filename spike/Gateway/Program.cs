var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<ForwardAuthorizationHandler>();
builder.Services.AddSpikeJwt();
builder.Services.AddHealthChecks();

// Per-subgraph HttpClient timeout. A stopped subgraph fails fast (DNS / connection refused);
// a hung (paused) one only fails via this timeout. Default HttpClient timeout is 100 s.
var timeout = TimeSpan.FromSeconds(
    builder.Configuration.GetValue("SUBGRAPH_TIMEOUT_SECONDS", 5));

var gateway = builder
    .AddGraphQLGateway()
    .AddFileSystemConfiguration(Path.Combine(AppContext.BaseDirectory, "gateway.far"))
    // HC16 "default security" disables introspection outside Development. The POC needs it for
    // the Nitro UI and the schema checks, so it is switched on explicitly.
    .DisableIntrospection(false)
    .ModifyRequestOptions(o =>
    {
        o.CollectOperationPlanTelemetry = true;
        // Standard GraphQL null propagation (plan §7). Pinned explicitly even though it is the default.
        o.DefaultErrorHandlingMode = HotChocolate.Language.ErrorHandlingMode.Propagate;
        // Spike-only switch for experiment 9: lets a request send "onError": "NULL" to compare modes.
        o.AllowErrorHandlingModeOverride = builder.Configuration.GetValue("ALLOW_ONERROR_OVERRIDE", false);
    });

// Source-schema names must equal the names used at composition (schema-settings.json "name").
// Each source schema gets a code-level client configuration whose HttpClient name equals the
// source-schema name, so the timeout and the header-forwarding handler apply per subgraph.
// Code-level configurations override the URL baked into the archive. There is deliberately NO
// fallback "fusion" client: an unconfigured one would have no header forwarding and a 100 s timeout.
foreach (var name in new[] { "Owner", "Extender" })
{
    var key = $"SUBGRAPH_{name.ToUpperInvariant()}_URL";
    var url = builder.Configuration[key]
        ?? throw new InvalidOperationException($"{key} is not set.");

    builder.Services
        .AddHttpClient(name, c => c.Timeout = timeout)
        .AddHttpMessageHandler<ForwardAuthorizationHandler>();

    gateway.AddHttpClientConfiguration(name, new Uri(url));
}

var app = builder.Build();
app.UseAuthentication();
app.Use(async (ctx, next) => // edge check: reject unauthenticated POSTs, keep the GET UI reachable
{
    if (ctx.Request.Path.StartsWithSegments("/graphql") && HttpMethods.IsPost(ctx.Request.Method)
        && ctx.User.Identity?.IsAuthenticated != true)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});
app.MapHealthChecks("/health");
app.MapGraphQL();
app.Run();

// Copies the caller's Authorization header onto every outgoing subgraph request.
public sealed class ForwardAuthorizationHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var auth = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) && request.Headers.Authorization is null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth);
        }
        return base.SendAsync(request, ct);
    }
}
