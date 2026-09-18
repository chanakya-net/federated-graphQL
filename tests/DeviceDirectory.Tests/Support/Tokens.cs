using SoR.Shared.Auth;
using SoR.Shared.Seeding;

namespace SoR.DeviceDirectory.Tests.Support;

/// <summary>Tokens for the contract users (contracts/tokens.json.md), minted with the committed <c>.env</c> key.</summary>
internal static class Tokens
{
    /// <summary>Valid length for HS256 but not the dev key: signature validation must fail.</summary>
    public const string OtherKey = "another-64-character-key-that-is-not-the-dev-key-0123456789abcd";

    public static string SigningKey { get; } = Repo.Env(DevAuth.SigningKeyEnv);

    public static string Alice { get; } = Create(SigningKey, "alice", "Alice (Tenant A)", SeedConstants.TenantA, DevAuth.Services.All);

    public static string Carol { get; } = Create(SigningKey, "carol", "Carol (Tenant A)", SeedConstants.TenantA, [DevAuth.Services.SoftwareInstall]);

    public static string Dave { get; } = Create(SigningKey, "dave", "Dave (Tenant B)", SeedConstants.TenantB, DevAuth.Services.All);

    public static string AliceSignedWithOtherKey { get; } =
        Create(OtherKey, "alice", "Alice (Tenant A)", SeedConstants.TenantA, DevAuth.Services.All);

    private static string Create(string key, string sub, string name, string tenantId, IEnumerable<string> services) =>
        DevTokenFactory.Create(key, sub, name, tenantId, services, DateTimeOffset.UtcNow.AddYears(5));
}
