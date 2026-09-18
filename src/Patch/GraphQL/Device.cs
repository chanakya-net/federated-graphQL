namespace SoR.Patch.GraphQL;

/// <summary>
/// The entity stub this subgraph extends. Device Directory owns <c>Device</c>; here it only carries the key
/// (<c>id</c>, implied by the <c>deviceById</c> lookup argument; no <c>@key</c>, docs/version-facts.md §2).
/// </summary>
public sealed record Device([property: ID] string Id);
