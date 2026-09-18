namespace SoR.SoftwareInstall.GraphQL;

/// <summary>
/// The entity stub this subgraph extends. Only the key; Device Directory owns everything else about a device.
/// </summary>
public sealed record Device([property: ID] string Id);
