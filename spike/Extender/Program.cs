using HotChocolate.Authorization;
using HotChocolate.Types.Composite;
using HotChocolate.Types.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSpikeJwt();
builder.Services.AddAuthorization(o => o.AddPolicy("ServiceAccess",
    p => p.RequireAuthenticatedUser().RequireClaim(SpikeAuth.ServicesClaim, "extender")));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();
builder
    .AddGraphQL("Extender")
    .AddAuthorization()
    .AddQueryType<Query>()
    .AddTypeExtension<DeviceExtensions>();

var app = builder.Build();
app.Use(async (ctx, next) => // experiment 3: prove the header arrives
{
    if (ctx.Request.Path.StartsWithSegments("/graphql"))
    {
        app.Logger.LogInformation(
            "Authorization header present: {Present} ({Method} {Path})",
            ctx.Request.Headers.ContainsKey("Authorization"), ctx.Request.Method, ctx.Request.Path);
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGraphQL();
await app.RunWithGraphQLCommandsAsync(args);

public sealed record Device([property: ID] string Id);

[Authorize]
public sealed class Query
{
    // Internal lookup: only the gateway may call it; it must NOT appear on the composite Query type.
    // Tenant scoping is omitted in the spike (Owner already scopes); the real subgraphs filter here too.
    [Lookup, Internal]
    public Device? GetDeviceById([ID] string id) => new(id);
}

[ExtendObjectType<Device>]
public sealed class DeviceExtensions
{
    // NULLABLE list on purpose (plan §4.2): an error here must not null out the parent Device.
    [Authorize(Policy = "ServiceAccess")]
    public IReadOnlyList<string>? GetNotes([Parent] Device device, [Service] IHttpContextAccessor http)
    {
        var tenant = http.HttpContext!.User.FindFirst(SpikeAuth.TenantClaim)?.Value;
        return [$"note for {device.Id} in {tenant}"];
    }

    // NON-NULL on purpose, spike only: experiment 9 uses it to show what happens when a guarded
    // field is non-null (the error propagates to the nearest nullable parent, i.e. `device`).
    [Authorize(Policy = "ServiceAccess")]
    public int GetNoteCount([Parent] Device device) => 1;
}
