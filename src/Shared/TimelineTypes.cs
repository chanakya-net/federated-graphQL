using HotChocolate.Types.Composite;

namespace SoR.Shared.Timeline;

/// <summary>
/// One normalized event emitted by any compatible timeline source. Every field is shareable because the
/// source schemas intentionally contribute the same semantic wire contract.
/// </summary>
public sealed record TimelineEvent(
    [property: ID, Shareable] string Id,
    [property: Shareable] DateTimeOffset OccurredAt,
    [property: Shareable] string Label,
    [property: Shareable] string Title,
    [property: Shareable] string Subtitle,
    [property: Shareable] string Status,
    [property: Shareable] string? Severity,
    [property: Shareable] IReadOnlyList<TimelineDetail> Details);

/// <summary>A presentation-neutral label/value row for the selected event.</summary>
public sealed record TimelineDetail(
    [property: Shareable] string Label,
    [property: Shareable] string Value,
    [property: Shareable] bool Mono);
