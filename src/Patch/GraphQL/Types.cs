namespace SoR.Patch.GraphQL;

// Hot Chocolate names enum values UPPER_CASE (Applied -> APPLIED), which are the contract names and also
// the strings stored in MongoDB (StoredEnum). Single-word members keep that mapping trivial.

public enum PatchStatus
{
    Applied,
    Failed,
    Pending,
}

public enum PatchSeverity
{
    Critical,
    High,
    Medium,
    Low,
}

/// <summary>
/// The contract's <c>Patch</c> type. Named <c>PatchInfo</c> in C# because <c>Patch</c> is also this project's
/// namespace (<c>SoR.Patch</c>), which would shadow the type everywhere outside <c>SoR.Patch.GraphQL</c>.
/// </summary>
[GraphQLName("Patch")]
[GraphQLDescription("A catalog patch.")]
public sealed record PatchInfo(
    [property: ID] string Id,
    string KbId,
    string Title,
    PatchSeverity Severity,
    string Vendor,
    DateTimeOffset ReleasedAt);

[GraphQLDescription("One patch applied to, failed on, or pending for a device.")]
public sealed record PatchEvent(
    [property: ID] string Id,
    [property: ID] string DeviceId,
    DateTimeOffset OccurredAt,
    PatchStatus Status,
    PatchInfo Patch);

/// <summary>Enum values as stored in MongoDB: the GraphQL / contract name (APPLIED, CRITICAL, ...).</summary>
public static class StoredEnum
{
    public static string ToStored<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();

    public static T Parse<T>(string stored) where T : struct, Enum => Enum.Parse<T>(stored, ignoreCase: true);
}
