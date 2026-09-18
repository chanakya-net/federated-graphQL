using HotChocolate.Authorization;
using HotChocolate.Types.Composite;
using HotChocolate.Types.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSpikeJwt();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();
builder
    .AddGraphQL("Owner")
    .AddAuthorization()
    .AddQueryType<Query>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGraphQL();
await app.RunWithGraphQLCommandsAsync(args); // enables: dotnet run -- schema export --output x.graphqls

public sealed record Device([property: ID] string Id, string Hostname, string TenantId);

[Authorize] // every field needs a valid token
public sealed class Query
{
    private static readonly Device[] Devices =
    [
        new("dev-00001", "alpha", "TenantA"),
        new("dev-00002", "beta", "TenantB"),
    ];

    // Public lookup: clients call it, and the gateway uses it to resolve Device by key.
    [Lookup]
    public Device? GetDevice([ID] string id, [Service] IHttpContextAccessor http)
    {
        var tenant = http.HttpContext!.User.FindFirst(SpikeAuth.TenantClaim)?.Value;
        return Devices.FirstOrDefault(d => d.Id == id && d.TenantId == tenant); // null, never an error, for another tenant
    }

    public IEnumerable<Device> GetDevices([Service] IHttpContextAccessor http)
    {
        var tenant = http.HttpContext!.User.FindFirst(SpikeAuth.TenantClaim)?.Value;
        return Devices.Where(d => d.TenantId == tenant);
    }
}
