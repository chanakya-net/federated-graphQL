using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SoR.Shared.Auth.Tests;

public sealed class DevAuthTests
{
    // Same value as DEV_JWT_SIGNING_KEY in the committed .env (64 ASCII chars).
    private const string Key = "sor-poc-dev-only-signing-key-do-not-use-in-prod-0123456789abcdef";
    private const string OtherKey = "another-64-character-key-that-is-not-the-dev-key-0123456789abcd";

    private static readonly DateTimeOffset FarFuture = DateTimeOffset.UtcNow.AddYears(5);

    [Fact]
    public async Task Token_validates_with_configured_parameters()
    {
        using var sp = BuildProvider(Key);
        var token = Token(Key, "alice", "TenantA", [DevAuth.Services.Patch]);

        var result = await Validate(sp, token);

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("alice", result.ClaimsIdentity.FindFirst(DevAuth.SubjectClaim)?.Value);
        Assert.Equal("TenantA", result.ClaimsIdentity.FindFirst(DevAuth.TenantClaim)?.Value);
        Assert.Equal("Alice", result.ClaimsIdentity.FindFirst(DevAuth.NameClaim)?.Value);
    }

    [Fact]
    public async Task Services_claim_is_multi_valued()
    {
        using var sp = BuildProvider(Key);
        var token = Token(Key, "bob", "TenantA", [DevAuth.Services.Patch, DevAuth.Services.Vulnerability]);

        var result = await Validate(sp, token);

        Assert.True(result.IsValid, result.Exception?.Message);
        var services = result.ClaimsIdentity.FindAll(DevAuth.ServicesClaim).Select(c => c.Value).Order().ToArray();
        Assert.Equal([DevAuth.Services.Patch, DevAuth.Services.Vulnerability], services);
    }

    [Fact]
    public void Services_is_a_json_array_even_with_one_entry()
    {
        // contracts/tokens.json.md: "services" is always a JSON array of strings.
        var token = Token(Key, "erin", "TenantB", [DevAuth.Services.Patch]);

        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(token.Split('.')[1]));
        var services = payload.RootElement.GetProperty(DevAuth.ServicesClaim);
        Assert.Equal(JsonValueKind.Array, services.ValueKind);
        Assert.Equal(DevAuth.Services.Patch, Assert.Single(services.EnumerateArray()).GetString());
    }

    [Fact]
    public void Token_header_and_registered_claims_match_contract()
    {
        var issuedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var token = new JsonWebToken(DevTokenFactory.Create(Key, "dave", "Dave", "TenantB",
            DevAuth.Services.All, issuedAt.AddYears(5), issuedAt));

        Assert.Equal(SecurityAlgorithms.HmacSha256, token.Alg);
        Assert.Equal("JWT", token.Typ);
        Assert.Equal(DevAuth.Issuer, token.Issuer);
        Assert.Equal([DevAuth.Audience], token.Audiences);
        Assert.Equal(issuedAt.UtcDateTime, token.IssuedAt);
        Assert.Equal(issuedAt.UtcDateTime, token.ValidFrom);
        Assert.Equal(issuedAt.AddYears(5).UtcDateTime, token.ValidTo);
    }

    [Fact]
    public async Task Wrong_key_fails()
    {
        using var sp = BuildProvider(Key);
        var token = Token(OtherKey, "alice", "TenantA", [DevAuth.Services.Patch]);

        var result = await Validate(sp, token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Expired_fails()
    {
        using var sp = BuildProvider(Key);
        var now = DateTimeOffset.UtcNow;
        var token = DevTokenFactory.Create(Key, "alice", "Alice", "TenantA", [DevAuth.Services.Patch],
            expires: now.AddDays(-1), issuedAt: now.AddDays(-2));

        var result = await Validate(sp, token);

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenExpiredException>(result.Exception);
    }

    [Fact]
    public void Short_key_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            DevTokenFactory.Create("0123456789abcdef", "alice", "Alice", "TenantA", [], FarFuture));
    }

    [Fact]
    public async Task Policy_allows_matching_service()
    {
        using var sp = BuildProvider(Key, policyFor: DevAuth.Services.Patch);
        var principal = await Principal(sp, Token(Key, "bob", "TenantA", [DevAuth.Services.Patch]));

        var result = await sp.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, DevAuth.ServiceAccessPolicy);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Policy_allows_matching_service_among_several()
    {
        using var sp = BuildProvider(Key, policyFor: DevAuth.Services.SoftwareInstall);
        var principal = await Principal(sp, Token(Key, "alice", "TenantA", DevAuth.Services.All));

        var result = await sp.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, DevAuth.ServiceAccessPolicy);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Policy_denies_missing_service()
    {
        using var sp = BuildProvider(Key, policyFor: DevAuth.Services.Patch);
        var principal = await Principal(sp, Token(Key, "carol", "TenantA", [DevAuth.Services.Vulnerability]));

        var result = await sp.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, DevAuth.ServiceAccessPolicy);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Policy_denies_anonymous()
    {
        using var sp = BuildProvider(Key, policyFor: DevAuth.Services.Patch);

        var result = await sp.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), DevAuth.ServiceAccessPolicy);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CallerContext_reads_claims()
    {
        using var sp = BuildProvider(Key);
        var principal = await Principal(sp, Token(Key, "bob", "TenantA", [DevAuth.Services.Patch, DevAuth.Services.Vulnerability]));

        var caller = CallerFor(sp, principal);

        Assert.True(caller.IsAuthenticated);
        Assert.Equal("bob", caller.UserId);
        Assert.Equal("TenantA", caller.TenantId);
        Assert.Equal(new HashSet<string> { DevAuth.Services.Patch, DevAuth.Services.Vulnerability }, caller.Services);
        Assert.True(caller.HasService(DevAuth.Services.Patch));
        Assert.False(caller.HasService(DevAuth.Services.SoftwareInstall));
    }

    [Fact]
    public void CallerContext_throws_without_tenant()
    {
        using var sp = BuildProvider(Key);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DevAuth.SubjectClaim, "nobody")], "test"));

        var caller = CallerFor(sp, principal);

        Assert.Throws<UnauthorizedAccessException>(() => caller.TenantId);
    }

    [Fact]
    public void CallerContext_without_http_context_is_anonymous()
    {
        using var sp = BuildProvider(Key);
        using var scope = sp.CreateScope();

        var caller = scope.ServiceProvider.GetRequiredService<ICallerContext>();

        Assert.False(caller.IsAuthenticated);
        Assert.Equal(string.Empty, caller.UserId);
        Assert.Empty(caller.Services);
    }

    [Fact]
    public async Task Missing_key_is_unhealthy()
    {
        using (var sp = BuildProvider(key: null))
        {
            var report = await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            Assert.Equal(HealthStatus.Unhealthy, report.Entries["auth-config"].Status);
        }

        using (var sp = BuildProvider(Key))
        {
            var report = await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            Assert.Equal(HealthStatus.Healthy, report.Entries["auth-config"].Status);
        }
    }

    [Fact]
    public async Task Missing_key_does_not_throw_and_rejects_real_tokens()
    {
        // Schema export runs without DEV_JWT_SIGNING_KEY: registration must succeed, and the
        // placeholder key must never validate a token signed with a real key.
        using var sp = BuildProvider(key: null);

        var result = await Validate(sp, Token(Key, "alice", "TenantA", DevAuth.Services.All));

        Assert.False(result.IsValid);
    }

    private static ServiceProvider BuildProvider(string? key, string? policyFor = null)
    {
        var settings = new Dictionary<string, string?>();
        if (key is not null) settings[DevAuth.SigningKeyEnv] = key;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDevJwtAuthentication(config);
        if (policyFor is not null) services.AddServiceAccessPolicy(policyFor);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static string Token(string key, string sub, string tenantId, IEnumerable<string> services) =>
        DevTokenFactory.Create(key, sub, char.ToUpperInvariant(sub[0]) + sub[1..], tenantId, services, FarFuture);

    private static TokenValidationParameters Parameters(IServiceProvider sp) =>
        sp.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .TokenValidationParameters;

    private static Task<TokenValidationResult> Validate(IServiceProvider sp, string token) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, Parameters(sp));

    private static async Task<ClaimsPrincipal> Principal(IServiceProvider sp, string token)
    {
        var result = await Validate(sp, token);
        Assert.True(result.IsValid, result.Exception?.Message);
        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

    private static ICallerContext CallerFor(ServiceProvider sp, ClaimsPrincipal principal)
    {
        sp.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = principal };
        return sp.CreateScope().ServiceProvider.GetRequiredService<ICallerContext>();
    }
}
