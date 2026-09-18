import fullAlice from '../../testing/fixtures/gateway/timeline-full-alice.json';
import type { InstallEvent, PatchEvent, VulnerabilityEvent } from '../graphql/types';
import type { TimelineSections } from './section-state';
import { applyFilter, fromInstall, fromPatch, fromVulnerability, merge } from './timeline-merge';
import { DEFAULT_FILTER, type TimelineEvent, type TimelineFilter } from './timeline.models';

const full = fullAlice.data.device as unknown as {
  patchEvents: PatchEvent[];
  vulnerabilityEvents: VulnerabilityEvent[];
  installEvents: InstallEvent[];
};

const okSections = (): TimelineSections => ({
  patchEvents: { kind: 'ok', events: full.patchEvents },
  vulnerabilityEvents: { kind: 'ok', events: full.vulnerabilityEvents },
  installEvents: { kind: 'ok', events: full.installEvents },
});

function event(id: string, occurredAt: string, extra: Partial<TimelineEvent> = {}): TimelineEvent {
  return {
    id,
    source: 'patch',
    occurredAt,
    label: id,
    title: id,
    subtitle: '',
    status: 'APPLIED',
    raw: null,
    ...extra,
  };
}

const filter = (change: Partial<TimelineFilter>): TimelineFilter => ({
  ...DEFAULT_FILTER,
  ...change,
});

describe('mapping', () => {
  it('patch: title, "<kbId> · <vendor>", status, severity, KB id as the label', () => {
    const e = fromPatch(full.patchEvents[0]);
    expect(e).toMatchObject({
      source: 'patch',
      label: 'KB5000128',
      title: 'Oracle security update KB5000128',
      subtitle: 'KB5000128 · Oracle',
      status: 'APPLIED',
      severity: 'HIGH',
    });
  });

  it('vulnerability: "<kind> <cve.id>", cve title, finding state, cve severity, CVE id as the label', () => {
    const v = full.vulnerabilityEvents[0];
    expect(fromVulnerability(v)).toMatchObject({
      source: 'vulnerability',
      label: v.cve.id,
      title: `${v.kind} ${v.cve.id}`,
      subtitle: v.cve.title,
      status: v.findingState,
      severity: v.cve.severity,
    });
  });

  it('install: "<action> <name> <version>", publisher, result, no severity, name as the label', () => {
    const i = full.installEvents[0];
    const e = fromInstall(i);
    expect(e).toMatchObject({
      source: 'softwareinstall',
      label: i.software.name,
      title: `${i.action} ${i.software.name} ${i.software.version}`,
      subtitle: i.software.publisher,
      status: i.result,
    });
    expect(e.severity).toBeUndefined();
  });
});

describe('merge', () => {
  it('concatenates all ok sections, newest first', () => {
    const events = merge(okSections());
    expect(events).toHaveLength(
      full.patchEvents.length + full.vulnerabilityEvents.length + full.installEvents.length,
    );
    for (let i = 1; i < events.length; i++) {
      expect(Date.parse(events[i - 1].occurredAt)).toBeGreaterThanOrEqual(
        Date.parse(events[i].occurredAt),
      );
    }
    expect(new Set(events.map((e) => e.source))).toEqual(
      new Set(['patch', 'vulnerability', 'softwareinstall']),
    );
  });

  it('leaves out sections that are not ok', () => {
    const events = merge({
      ...okSections(),
      installEvents: { kind: 'no-access' },
      patchEvents: { kind: 'unavailable', message: 'x' },
    });
    expect(events.every((e) => e.source === 'vulnerability')).toBe(true);
    expect(events).toHaveLength(full.vulnerabilityEvents.length);
  });

  it('sorts descending and breaks ties by id, whatever the input order (stable)', () => {
    const at = '2026-08-01T10:00:00Z';
    const patch = (id: string, occurredAt: string) => ({ ...full.patchEvents[0], id, occurredAt });
    const input = [
      patch('p-b', at),
      patch('p-old', '2026-07-01T00:00:00Z'),
      patch('p-a', at),
      patch('p-new', '2026-09-01T00:00:00Z'),
    ];
    const expected = ['p-new', 'p-a', 'p-b', 'p-old'];
    for (const events of [input, [...input].reverse()]) {
      const merged = merge({
        patchEvents: { kind: 'ok', events },
        vulnerabilityEvents: { kind: 'ok', events: [] },
        installEvents: { kind: 'ok', events: [] },
      });
      expect(merged.map((e) => e.id)).toEqual(expected);
    }
  });

  it('compares instants, not strings (offsets)', () => {
    const patch = (id: string, occurredAt: string) => ({ ...full.patchEvents[0], id, occurredAt });
    const merged = merge({
      patchEvents: {
        kind: 'ok',
        events: [patch('utc', '2026-08-01T09:00:00Z'), patch('plus2', '2026-08-01T10:30:00+02:00')],
      },
      vulnerabilityEvents: { kind: 'ok', events: [] },
      installEvents: { kind: 'ok', events: [] },
    });
    expect(merged.map((e) => e.id)).toEqual(['utc', 'plus2']); // 10:30+02:00 is 08:30Z
  });
});

describe('applyFilter', () => {
  const events: TimelineEvent[] = [
    event('p1', '2026-08-10T00:00:00Z', {
      source: 'patch',
      status: 'FAILED',
      title: 'Oracle update',
      subtitle: 'KB1 · Oracle',
    }),
    event('v1', '2026-08-05T00:00:00Z', {
      source: 'vulnerability',
      status: 'OPEN',
      title: 'DETECTED CVE-1',
      subtitle: 'Chromium overflow',
    }),
    event('i1', '2026-07-20T00:00:00Z', {
      source: 'softwareinstall',
      status: 'FAILED',
      title: 'INSTALL Python 3',
      subtitle: 'PSF',
    }),
    event('i2', '2026-08-01T00:00:00Z', {
      source: 'softwareinstall',
      status: 'SUCCESS',
      title: 'UPGRADE Git 2',
      subtitle: 'Git',
    }),
  ];
  const ids = (f: TimelineFilter) => applyFilter(events, f).map((e) => e.id);

  it('default filter keeps everything', () => {
    expect(ids(DEFAULT_FILTER)).toEqual(['p1', 'v1', 'i1', 'i2']);
  });

  it('sources', () => {
    expect(ids(filter({ sources: new Set(['softwareinstall']) }))).toEqual(['i1', 'i2']);
    expect(ids(filter({ sources: new Set() }))).toEqual([]);
  });

  it('since / until, inclusive', () => {
    expect(ids(filter({ since: '2026-08-01T00:00:00Z' }))).toEqual(['p1', 'v1', 'i2']);
    expect(ids(filter({ until: '2026-08-01T00:00:00Z' }))).toEqual(['i1', 'i2']);
    expect(ids(filter({ since: '2026-08-01T00:00:00Z', until: '2026-08-05T00:00:00Z' }))).toEqual([
      'v1',
      'i2',
    ]);
  });

  it('statuses (empty = all; FAILED spans sources)', () => {
    expect(ids(filter({ statuses: new Set(['FAILED']) }))).toEqual(['p1', 'i1']);
    expect(ids(filter({ statuses: new Set(['OPEN', 'SUCCESS']) }))).toEqual(['v1', 'i2']);
  });

  it('text: case-insensitive over title and subtitle, trimmed', () => {
    expect(ids(filter({ text: 'oracle' }))).toEqual(['p1']);
    expect(ids(filter({ text: '  CHROMIUM ' }))).toEqual(['v1']);
    expect(ids(filter({ text: 'nothing like this' }))).toEqual([]);
  });

  it('all dimensions combined', () => {
    const combined = filter({
      sources: new Set(['patch', 'softwareinstall']),
      since: '2026-07-01T00:00:00Z',
      until: '2026-08-31T23:59:59.999Z',
      statuses: new Set(['FAILED']),
      text: 'python',
    });
    expect(ids(combined)).toEqual(['i1']);
  });
});
