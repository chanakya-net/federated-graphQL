import type { InstallEvent, PatchEvent, VulnerabilityEvent } from '../graphql/types';
import type { TimelineSections } from './section-state';
import type { TimelineEvent, TimelineFilter } from './timeline.models';

// Pure: map, merge, sort and filter. Only sections in the `ok` state contribute events.

export function fromPatch(e: PatchEvent): TimelineEvent {
  return {
    id: e.id,
    source: 'patch',
    occurredAt: e.occurredAt,
    title: e.patch.title,
    subtitle: `${e.patch.kbId} · ${e.patch.vendor}`,
    status: e.status,
    severity: e.patch.severity,
    raw: e,
  };
}

export function fromVulnerability(e: VulnerabilityEvent): TimelineEvent {
  return {
    id: e.id,
    source: 'vulnerability',
    occurredAt: e.occurredAt,
    title: `${e.kind} ${e.cve.id}`,
    subtitle: e.cve.title,
    status: e.findingState,
    severity: e.cve.severity,
    raw: e,
  };
}

export function fromInstall(e: InstallEvent): TimelineEvent {
  return {
    id: e.id,
    source: 'softwareinstall',
    occurredAt: e.occurredAt,
    title: `${e.action} ${e.software.name} ${e.software.version}`,
    subtitle: e.software.publisher,
    status: e.result,
    raw: e,
  };
}

/** Newest first; equal instants ordered by id so the order never depends on the input order. */
function compareEvents(a: TimelineEvent, b: TimelineEvent): number {
  const byTime = Date.parse(b.occurredAt) - Date.parse(a.occurredAt);
  if (byTime !== 0) return byTime;
  return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
}

export function merge(sections: TimelineSections): TimelineEvent[] {
  const { patchEvents: p, vulnerabilityEvents: v, installEvents: i } = sections;
  return [
    ...(p.kind === 'ok' ? p.events.map(fromPatch) : []),
    ...(v.kind === 'ok' ? v.events.map(fromVulnerability) : []),
    ...(i.kind === 'ok' ? i.events.map(fromInstall) : []),
  ].sort(compareEvents);
}

export function applyFilter(
  events: readonly TimelineEvent[],
  filter: TimelineFilter,
): TimelineEvent[] {
  const since = filter.since ? Date.parse(filter.since) : -Infinity;
  const until = filter.until ? Date.parse(filter.until) : Infinity;
  const text = filter.text.trim().toLowerCase();
  return events.filter((e) => {
    if (!filter.sources.has(e.source)) return false;
    const at = Date.parse(e.occurredAt);
    if (at < since || at > until) return false;
    if (filter.statuses.size > 0 && !filter.statuses.has(e.status)) return false;
    if (text && !`${e.title}\n${e.subtitle}`.toLowerCase().includes(text)) return false;
    return true;
  });
}
