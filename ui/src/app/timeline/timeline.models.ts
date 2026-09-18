import type { SectionKey } from './section-state';

export type Source = 'patch' | 'vulnerability' | 'softwareinstall';
export const SOURCES: readonly Source[] = ['patch', 'vulnerability', 'softwareinstall'];

export interface TimelineEvent {
  id: string;
  source: Source;
  occurredAt: string;
  title: string;
  subtitle: string;
  /** APPLIED|FAILED|PENDING (patch), OPEN|REMEDIATED (vulnerability), SUCCESS|FAILED (install). */
  status: string;
  /** Patch / CVE severity when present. */
  severity?: string;
  raw: unknown;
}

export interface TimelineFilter {
  /** Default: all three. */
  sources: ReadonlySet<Source>;
  /** ISO-8601, inclusive. Also sent to the server as `$since` / `$until`. */
  since?: string;
  until?: string;
  /** Empty = all. */
  statuses: ReadonlySet<string>;
  /** Case-insensitive substring over title + subtitle. */
  text: string;
}

/** Every status value the three sources produce, in filter order. FAILED covers patch and install. */
export const STATUSES: readonly string[] = [
  'APPLIED',
  'PENDING',
  'FAILED',
  'SUCCESS',
  'OPEN',
  'REMEDIATED',
];

export const DEFAULT_FILTER: TimelineFilter = {
  sources: new Set(SOURCES),
  statuses: new Set(),
  text: '',
};

export interface SectionMeta {
  key: SectionKey;
  source: Source;
  /** Display name used in banners and filters. */
  name: string;
  /** Material icon for events of this source. */
  icon: string;
  /** Plural noun for the unavailable banner ("patch history is not shown"). */
  history: string;
}

export const SECTIONS: readonly SectionMeta[] = [
  {
    key: 'patchEvents',
    source: 'patch',
    name: 'Patch',
    icon: 'system_update_alt',
    history: 'patch',
  },
  {
    key: 'vulnerabilityEvents',
    source: 'vulnerability',
    name: 'Vulnerability',
    icon: 'bug_report',
    history: 'vulnerability',
  },
  {
    key: 'installEvents',
    source: 'softwareinstall',
    name: 'Software Install',
    icon: 'apps',
    history: 'install',
  },
];

export function sourceMeta(source: Source): SectionMeta {
  return SECTIONS.find((s) => s.source === source)!;
}
