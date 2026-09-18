using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace SoR.Shared.Auth;

public static class DevAuthServiceCollectionExtensions
{
    /// <summary>
    /// JWT Bearer validation with the shared dev key. Never throws at startup; a missing key
    /// is surfaced by <see cref="DevAuthHealthCheck"/> so <c>schema export</c> keeps working without config.
    /// </summary>
    public static IServiceCollection AddDevJwtAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var key = config[DevAuth.SigningKeyEnv];
        var effective = string.IsNullOrWhiteSpace(key) ? DevAuth.MissingKeyPlaceholder : key;

        services.AddSingleton(new DevAuthState(KeyConfigured: !string.IsNullOrWhiteSpace(key)));
        services.AddHttpContextAccessor();
        services.AddScoped<ICallerContext, HttpCallerContext>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;   // keep "sub", "tenantId", "services" as-is
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = DevAuth.Issuer,
                    ValidAudience = DevAuth.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(effective)),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });
        services.AddHealthChecks().AddCheck<DevAuthHealthCheck>("auth-config");
        return services;
    }

    /// <summary>
    /// Registers the <see cref="DevAuth.ServiceAccessPolicy"/> policy for one domain service name (e.g. "patch").
    /// The JWT's <c>services</c> array arrives as one claim per entry, so RequireClaim matches any of them.
    /// </summary>
    public static IServiceCollection AddServiceAccessPolicy(this IServiceCollection services, string serviceName)
    {
        services.AddAuthorization(o => o.AddPolicy(DevAuth.ServiceAccessPolicy,
            p => p.RequireAuthenticatedUser().RequireClaim(DevAuth.ServicesClaim, serviceName)));
        return services;
    }
}
