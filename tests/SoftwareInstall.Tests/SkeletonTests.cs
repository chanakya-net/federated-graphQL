using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SoR.SoftwareInstall.Tests;

// Phase 1 placeholder: proves the skeleton starts and answers /health. Lane P3C replaces it.
public sealed class SkeletonTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_200()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
