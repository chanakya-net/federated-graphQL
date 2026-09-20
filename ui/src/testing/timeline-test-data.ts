import type {
  TimelineCatalog,
  TimelineEventWire,
  TimelineSource,
} from '../app/timeline/timeline-catalog';

export const TIMELINE_SOURCES: readonly TimelineSource[] = [
  {
    id: 'patch',
    field: 'patchTimeline',
    name: 'Patch',
    icon: 'system_update_alt',
    color: '#1d4ed8',
    history: 'patch',
    statuses: ['APPLIED', 'PENDING', 'FAILED'],
    contractVersion: 1,
  },
  {
    id: 'vulnerability',
    field: 'vulnerabilityTimeline',
    name: 'Vulnerability',
    icon: 'bug_report',
    color: '#be185d',
    history: 'vulnerability',
    statuses: ['OPEN', 'REMEDIATED'],
    contractVersion: 1,
  },
  {
    id: 'softwareinstall',
    field: 'softwareInstallTimeline',
    name: 'Software Install',
    icon: 'apps',
    color: '#047857',
    history: 'install',
    statuses: ['SUCCESS', 'FAILED'],
    contractVersion: 1,
  },
];

export const FOURTH_SOURCE: TimelineSource = {
  id: 'certificate history',
  field: 'certificateTimeline',
  name: 'Certificate',
  icon: 'workspace_premium',
  color: '#7c3aed',
  history: 'certificate',
  statuses: ['ISSUED', 'EXPIRED'],
  contractVersion: 1,
};

export const catalog = (sources: readonly TimelineSource[] = TIMELINE_SOURCES): TimelineCatalog => ({
  version: 1,
  schemaHash: 'a'.repeat(64),
  sources,
});

export const wireEvent = (
  id: string,
  change: Partial<TimelineEventWire> = {},
): TimelineEventWire => ({
  id,
  occurredAt: '2026-08-25T10:22:00Z',
  label: id,
  title: `Title ${id}`,
  subtitle: `Subtitle ${id}`,
  status: 'SUCCESS',
  severity: null,
  details: [
    { label: 'Serial', value: `serial-${id}`, mono: true },
    { label: 'Event ID', value: id, mono: true },
  ],
  ...change,
});

export const device = {
  id: 'dev-00001',
  hostname: 'bandwidth-grnf-001',
  os: 'Windows 10 22H2',
  ipAddress: '241.25.164.7',
  lastSeenAt: '2026-08-21T09:03:00Z',
  tenantId: 'TenantA',
};

export function timelineBody(
  sources: readonly TimelineSource[],
  values: Readonly<Record<string, TimelineEventWire[] | null>>,
  failures: Readonly<Record<string, { message: string; code?: string }>> = {},
) {
  const dynamic: Record<string, TimelineEventWire[] | null> = {};
  const errors: {
    message: string;
    path: (string | number)[];
    extensions?: { code: string };
  }[] = [];
  sources.forEach((source, index) => {
    const alias = `timelineSource${index}`;
    dynamic[alias] = Object.hasOwn(values, source.id) ? values[source.id] : [];
    const failure = Object.hasOwn(failures, source.id) ? failures[source.id] : undefined;
    if (failure) {
      dynamic[alias] = null;
      errors.push({
        message: failure.message,
        path: ['device', alias],
        ...(failure.code ? { extensions: { code: failure.code } } : {}),
      });
    }
  });
  return {
    ...(errors.length ? { errors } : {}),
    data: { device: { ...device, ...dynamic } },
  };
}
