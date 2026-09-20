using SoR.Shared.Timeline;

namespace SoR.SoftwareInstall.GraphQL;

// Exactly the contract's types (contracts/software-install.graphqls). C# member names map to the contract's enum
// values (Install -> INSTALL); the blob JSON uses the same names (InstallJson). Declaration order = contract order.

public enum InstallAction
{
    Install,
    Uninstall,
    Upgrade,
}

public enum InstallResult
{
    Success,
    Failed,
}

public sealed record Software(string Name, string Version, string Publisher);

public sealed record InstallEvent(
    [property: ID] string Id,
    [property: ID] string DeviceId,
    DateTimeOffset OccurredAt,
    InstallAction Action,
    InstallResult Result,
    Software Software);

internal static class TimelineAdapters
{
    public static TimelineEvent FromInstall(InstallEvent e)
    {
        var action = e.Action.ToString().ToUpperInvariant();
        var result = e.Result.ToString().ToUpperInvariant();
        return new TimelineEvent(
            e.Id,
            e.OccurredAt,
            e.Software.Name,
            $"{action} {e.Software.Name} {e.Software.Version}",
            e.Software.Publisher,
            result,
            null,
            [
                new("Action", action, false),
                new("Software", e.Software.Name, false),
                new("Version", e.Software.Version, true),
                new("Publisher", e.Software.Publisher, false),
                new("Result", result, false),
                new("Event ID", e.Id, true),
            ]);
    }
}

/// <summary>The contract's <c>SoftwareKeyInput</c>: an exact name and optionally one exact version (null = any version).</summary>
[GraphQLDescription("A product to look devices up by: the exact name, and optionally one exact version (null = any version).")]
public sealed record SoftwareKeyInput(string Name, string? Version);

/// <summary>
/// One page of the reverse lookup (<c>Query.devicesWithSoftware</c>). <see cref="Device"/> is the entity stub: the
/// gateway completes hostname, os, ... through Device Directory's <c>device(id)</c> lookup.
/// </summary>
[GraphQLDescription("One page of devices with install events for the selected software, ordered by device id, plus the unpaged count.")]
public sealed record SoftwareDeviceMatches(IReadOnlyList<SoftwareDeviceMatch> Items, int TotalCount)
{
    /// <summary>The selection this result answers; <see cref="SoftwareDeviceMatchesExtensions.GetMatches"/> resolves the per-key device sets from it.</summary>
    [GraphQLIgnore]
    public IReadOnlyList<Storage.SoftwareKey> Keys { get; init; } = [];
}

[GraphQLDescription("A device and its install events for the selected software, newest first.")]
public sealed record SoftwareDeviceMatch(Device Device, IReadOnlyList<InstallEvent> Events);

/// <summary>
/// The device set of one selected software key (ids only): what a client needs to combine selections with AND / OR
/// across subgraphs, which the gateway cannot do. An unknown product or version gets an empty set.
/// </summary>
[GraphQLDescription("The ids of the caller's devices with an install event for one selected software key (name, and version unless null = any), sorted, at most 10000.")]
public sealed record SoftwareMatch(string Name, string? Version, [property: ID] IReadOnlyList<string> DeviceIds);
