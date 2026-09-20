using HotChocolate.Types.Composite;

namespace SoR.DeviceSearch.GraphQL;

public sealed record DeviceSearchFilterInput(string Category, string Key, string Connector = "and");
public sealed record Device([property: ID, Shareable] string Id);
public sealed record DeviceSearchEvent(
    [property: ID] string Id, string Source, string ItemKey, DateTimeOffset? OccurredAt,
    string Label, string Title, string Subtitle, string Status, string? Severity);
public sealed record DeviceSearchItem(Device Device, IReadOnlyList<DeviceSearchEvent> Events);
public sealed record FindDevicesResult(IReadOnlyList<DeviceSearchItem> Items, int TotalCount, bool HasNextPage);

public sealed record SearchCapability(string Category, string Name, string Icon, string Color, string Placeholder, string FilterKind, bool Available);
public sealed record SearchCatalogItem(string Key, string Label, string Detail);
