namespace SoR.Shared.Auth;

public static class DevAuth
{
    public const string SigningKeyEnv = "DEV_JWT_SIGNING_KEY";
    public const string Issuer = "sor-poc";
    public const string Audience = "sor-poc";
    public const string TenantClaim = "tenantId";
    public const string ServicesClaim = "services";
    public const string SubjectClaim = "sub";
    public const string NameClaim = "name";
    public const string ServiceAccessPolicy = "ServiceAccess";

    /// <summary>Values of the <c>services</c> claim, one per domain subgraph.</summary>
    public static class Services
    {
        public const string Patch = "patch";
        public const string Vulnerability = "vulnerability";
        public const string SoftwareInstall = "softwareinstall";
        public static readonly string[] All = [Patch, Vulnerability, SoftwareInstall];
    }

    /// <summary>
    /// Used only when the env var is absent (schema export, design time), so startup never throws.
    /// <see cref="DevAuthHealthCheck"/> reports the missing key.
    /// </summary>
    public const string MissingKeyPlaceholder = "MISSING-DEV_JWT_SIGNING_KEY-placeholder-0123456789abcdef0123456789";
}
