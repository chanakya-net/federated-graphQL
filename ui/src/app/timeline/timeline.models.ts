import type { TimelineDetail, TimelineEventWire, TimelineSource } from './timeline-catalog';

export interface TimelineEvent extends TimelineEventWire {
  /** Assigned by the client from the catalog entry that selected this event. */
  source: string;
  sourceMeta: TimelineSource;
  details: TimelineDetail[];
}

/** Unique across sources even when ids contain punctuation or separators. */
export function eventKey(e: Pick<TimelineEvent, 'source' | 'id'>): string {
  return JSON.stringify([e.source, e.id]);
}

export interface TimelineFilter {
  sources: ReadonlySet<string>;
  /** ISO-8601, inclusive. Also sent to the server as `$since` / `$until`. */
  since?: string;
  until?: string;
  /** Empty = all. */
  statuses: ReadonlySet<string>;
  /** Case-insensitive substring over title + subtitle. */
  text: string;
}

export function defaultTimelineFilter(sources: readonly TimelineSource[] = []): TimelineFilter {
  return {
    sources: new Set(sources.map((source) => source.id)),
    statuses: new Set(),
    text: '',
  };
}
