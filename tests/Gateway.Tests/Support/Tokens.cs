using SoR.Shared.Auth;

namespace SoR.Gateway.Tests.Support;

/// <summary>Tokens for the contract users (contracts/tokens.json.md), minted with the committed <c>.env</c> key.</summary>
internal static class Tokens
{
    /// <summary>Valid length for HS256 but not the dev key: signature validation must fail.</summary>
    public const string OtherKey = "another-64-character-key-that-is-not-the-dev-key-0123456789abcd";

    public static string SigningKey { get; } = Repo.Env(DevAuth.SigningKeyEnv)
        ?? throw new InvalidOperationException($"{DevAuth.SigningKeyEnv} missing from .env");

    /// <summary>TenantA, all three services.</summary>
    public static string Alice { get; } = Create(SigningKey, "alice", "TenantA", DevAuth.Services.All);

    /// <summary>TenantA, patch + vulnerability: no software install access.</summary>
    public static string Bob { get; } = Create(SigningKey, "bob", "TenantA", [DevAuth.Services.Patch, DevAuth.Services.Vulnerability]);

    /// <summary>TenantB, all three services.</summary>
    public static string Dave { get; } = Create(SigningKey, "dave", "TenantB", DevAuth.Services.All);

    public static string AliceSignedWithOtherKey { get; } = Create(OtherKey, "alice", "TenantA", DevAuth.Services.All);

    /// <summary>Expired ten minutes ago, beyond the one-minute clock skew.</summary>
    public static string AliceExpired { get; } = DevTokenFactory.Create(SigningKey, "alice", "alice", "TenantA", DevAuth.Services.All,
        expires: DateTimeOffset.UtcNow.AddMinutes(-10), issuedAt: DateTimeOffset.UtcNow.AddHours(-1));

    private static string Create(string key, string sub, string tenantId, IEnumerable<string> services) =>
        DevTokenFactory.Create(key, sub, sub, tenantId, services, DateTimeOffset.UtcNow.AddYears(5));
}
