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

/// <summary>
/// One page of the reverse lookup (<c>Query.devicesWithPatches</c>). <see cref="Device"/> is the entity stub: the
/// gateway resolves hostname, os, ... through Device Directory's <c>device(id)</c> lookup.
/// </summary>
[GraphQLDescription("One page of devices with events for the selected patches, ordered by device id, plus the unpaged count.")]
public sealed record PatchDeviceMatches(IReadOnlyList<PatchDeviceMatch> Items, int TotalCount)
{
    /// <summary>The selection this result answers; <see cref="PatchDeviceMatchesExtensions.GetMatches"/> resolves the per-patch device sets from it.</summary>
    [GraphQLIgnore]
    public IReadOnlyList<string> PatchIds { get; init; } = [];
}

[GraphQLDescription("A device and its events for the selected patches, newest first.")]
public sealed record PatchDeviceMatch(Device Device, IReadOnlyList<PatchEvent> Events);

/// <summary>
/// The device set of one selected patch (ids only): what a client needs to combine selections with AND / OR across
/// subgraphs, which the gateway cannot do. Unknown ids get an empty set.
/// </summary>
[GraphQLDescription("The ids of the caller's devices with an event for one selected patch, sorted, at most 10000.")]
public sealed record PatchMatch([property: ID] string PatchId, [property: ID] IReadOnlyList<string> DeviceIds);

/// <summary>Enum values as stored in MongoDB: the GraphQL / contract name (APPLIED, CRITICAL, ...).</summary>
public static class StoredEnum
{
    public static string ToStored<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();

    public static T Parse<T>(string stored) where T : struct, Enum => Enum.Parse<T>(stored, ignoreCase: true);
}
