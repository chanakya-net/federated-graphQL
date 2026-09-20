using HotChocolate.AspNetCore;
using SoR.DeviceSearch.GraphQL;
using SoR.DeviceSearch.Search;
using SoR.DeviceSearch.Providers;
using SoR.DeviceSearch.Transport;
using SoR.Shared.Auth;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDevJwtAuthentication(builder.Configuration);
builder.Services.AddSingleton<ISearchProvider, PatchSearchProvider>();
builder.Services.AddSingleton<ISearchProvider, VulnerabilitySearchProvider>();
builder.Services.AddSingleton<ISearchProvider, SoftwareInstallSearchProvider>();
builder.Services.AddSingleton<SearchProviderRegistry>();
builder.Services.AddScoped<DeviceSearchEngine>();
builder.Services.AddHttpClient<IDomainSearchClient, DomainSearchClient>();
builder.AddGraphQL(DeviceSearchSchema.Name)
    .AddDeviceSearchTypes()
    .ModifyRequestOptions(o => o.IncludeExceptionDetails = builder.Environment.IsDevelopment());
var app = builder.Build();
_ = app.Services.GetRequiredService<SearchProviderRegistry>();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGraphQL().WithOptions(o => o.Batching = AllowedBatching.VariableBatching);
await app.RunWithGraphQLCommandsAsync(args);
