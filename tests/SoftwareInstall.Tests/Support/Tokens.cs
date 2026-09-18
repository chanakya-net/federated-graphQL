using SoR.Shared.Auth;
using SoR.Shared.Seeding;

namespace SoR.SoftwareInstall.Tests.Support;

/// <summary>Tokens for the contract users (contracts/tokens.json.md), minted with the committed <c>.env</c> key.</summary>
internal static class Tokens
{
    /// <summary>Valid length for HS256 but not the dev key: signature validation must fail.</summary>
    public const string OtherKey = "another-64-character-key-that-is-not-the-dev-key-0123456789abcd";

    public static string SigningKey { get; } = Repo.Env(DevAuth.SigningKeyEnv);

    /// <summary>TenantA; patch, vulnerability, softwareinstall.</summary>
    public static string Alice { get; } = Create(SigningKey, "alice", "Alice (Tenant A)", SeedConstants.TenantA, DevAuth.Services.All);

    /// <summary>TenantA; patch, vulnerability (no softwareinstall).</summary>
    public static string Bob { get; } = Create(SigningKey, "bob", "Bob (Tenant A)", SeedConstants.TenantA,
        [DevAuth.Services.Patch, DevAuth.Services.Vulnerability]);

    /// <summary>TenantA; softwareinstall only.</summary>
    public static string Carol { get; } = Create(SigningKey, "carol", "Carol (Tenant A)", SeedConstants.TenantA, [DevAuth.Services.SoftwareInstall]);

    /// <summary>TenantB; patch, vulnerability, softwareinstall.</summary>
    public static string Dave { get; } = Create(SigningKey, "dave", "Dave (Tenant B)", SeedConstants.TenantB, DevAuth.Services.All);

    /// <summary>TenantB; patch only.</summary>
    public static string Erin { get; } = Create(SigningKey, "erin", "Erin (Tenant B)", SeedConstants.TenantB, [DevAuth.Services.Patch]);

    public static string AliceSignedWithOtherKey { get; } =
        Create(OtherKey, "alice", "Alice (Tenant A)", SeedConstants.TenantA, DevAuth.Services.All);

    public static string For(string user) => user switch
    {
        "alice" => Alice,
        "bob" => Bob,
        "carol" => Carol,
        "dave" => Dave,
        "erin" => Erin,
        _ => throw new ArgumentOutOfRangeException(nameof(user), user, "not a contract user"),
    };

    private static string Create(string key, string sub, string name, string tenantId, IEnumerable<string> services) =>
        DevTokenFactory.Create(key, sub, name, tenantId, services, DateTimeOffset.UtcNow.AddYears(5));
}
