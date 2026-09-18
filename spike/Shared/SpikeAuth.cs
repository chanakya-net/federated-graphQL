// Shared constants for the spike. Phase 1 turns these into Shared.Auth.
public static class SpikeAuth
{
    public const string Issuer = "sor-poc";
    public const string Audience = "sor-poc";
    public const string TenantClaim = "tenantId";
    public const string ServicesClaim = "services";

    // HS256 needs >= 256 bits. This is 64 ASCII chars. Dev-only, committed on purpose.
    public const string Key = "sor-poc-dev-only-signing-key-do-not-use-in-prod-0123456789abcdef";
}
