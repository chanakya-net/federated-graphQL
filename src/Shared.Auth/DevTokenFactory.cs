using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SoR.Shared.Auth;

/// <summary>Single token implementation, used by TokenGenerator and by every test project.</summary>
public static class DevTokenFactory
{
    /// <summary>Minimum HS256 key size. Shorter keys fail in Microsoft.IdentityModel with IDX10720.</summary>
    public const int MinKeyBytes = 32;

    public static string Create(string signingKey, string sub, string name, string tenantId,
        IEnumerable<string> services, DateTimeOffset expires, DateTimeOffset? issuedAt = null)
    {
        if (Encoding.UTF8.GetByteCount(signingKey) < MinKeyBytes)
            throw new ArgumentException($"signing key must be at least {MinKeyBytes} bytes (256 bits) for HS256", nameof(signingKey));

        var now = (issuedAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = DevAuth.Issuer,
            Audience = DevAuth.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [DevAuth.SubjectClaim] = sub,
                [DevAuth.NameClaim] = name,
                [DevAuth.TenantClaim] = tenantId,
                [DevAuth.ServicesClaim] = services.Distinct(StringComparer.Ordinal).ToArray(),   // always a JSON array
            },
        });
    }
}
