import type { TimelineSections } from './section-state';
import type { TimelineEvent, TimelineFilter } from './timeline.models';

/** Newest first; equal instants use source then id so input order cannot affect the result. */
function compareEvents(a: TimelineEvent, b: TimelineEvent): number {
  const byTime = Date.parse(b.occurredAt) - Date.parse(a.occurredAt);
  if (byTime !== 0) return byTime;
  const bySource = a.source.localeCompare(b.source);
  return bySource || a.id.localeCompare(b.id);
}

export function merge(sections: TimelineSections): TimelineEvent[] {
  return Object.values(sections)
    .flatMap((section) => (section.kind === 'ok' ? section.events : []))
    .sort(compareEvents);
}

export function applyFilter(
  events: readonly TimelineEvent[],
  filter: TimelineFilter,
): TimelineEvent[] {
  const since = filter.since ? Date.parse(filter.since) : -Infinity;
  const until = filter.until ? Date.parse(filter.until) : Infinity;
  const text = filter.text.trim().toLowerCase();
  return events.filter((event) => {
    if (!filter.sources.has(event.source)) return false;
    const at = Date.parse(event.occurredAt);
    if (at < since || at > until) return false;
    if (filter.statuses.size > 0 && !filter.statuses.has(event.status)) return false;
    if (text && !`${event.title}\n${event.subtitle}`.toLowerCase().includes(text)) return false;
    return true;
  });
}
