namespace SoR.TokenGenerator;

/// <summary>One element of <c>users.json</c>.</summary>
public sealed record UserSpec(string Sub, string Name, string TenantId, IReadOnlyList<string> Services);

/// <summary>
/// One element of <c>tokens.json</c> (contracts/tokens.json.md). Declaration order is the JSON property order:
/// <c>sub</c>, <c>name</c>, <c>tenantId</c>, <c>services</c>, <c>token</c>.
/// </summary>
public sealed record TokenEntry(string Sub, string Name, string TenantId, IReadOnlyList<string> Services, string Token);
