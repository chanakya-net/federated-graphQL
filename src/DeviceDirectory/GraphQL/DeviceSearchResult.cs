using SoR.DeviceDirectory.Data;

namespace SoR.DeviceDirectory.GraphQL;

/// <summary>One page of the caller's devices plus the unpaged match count.</summary>
public sealed record DeviceSearchResult(IReadOnlyList<DeviceEntity> Items, int TotalCount);
