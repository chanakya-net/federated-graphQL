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
