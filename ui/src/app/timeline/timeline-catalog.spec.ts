import { print } from 'graphql';

import { buildTimelineQuery, validateTimelineCatalog } from './timeline-catalog';

const FOUR_SOURCES = {
  version: 1,
  schemaHash: 'a'.repeat(64),
  sources: [
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
    {
      id: 'certificate history',
      field: 'certificateTimeline',
      name: 'Certificate',
      icon: 'workspace_premium',
      color: '#7c3aed',
      history: 'certificate',
      statuses: ['ISSUED', 'EXPIRED'],
      contractVersion: 1,
    },
  ],
};

describe('timeline catalog and query', () => {
  it('validates a fourth source and builds a safe uniform AST query', () => {
    const catalog = validateTimelineCatalog(FOUR_SOURCES);
    const built = buildTimelineQuery(catalog);
    const query = print(built.document);

    expect(built.fields.map((field) => [field.alias, field.source.id, field.source.field])).toEqual([
      ['timelineSource0', 'patch', 'patchTimeline'],
      ['timelineSource1', 'vulnerability', 'vulnerabilityTimeline'],
      ['timelineSource2', 'softwareinstall', 'softwareInstallTimeline'],
      ['timelineSource3', 'certificate history', 'certificateTimeline'],
    ]);
    expect(query).toContain('timelineSource3: certificateTimeline(since: $since, until: $until)');
    expect(query).toContain('details {\n        label\n        value\n        mono');
    expect(query).not.toContain('certificate history');
  });

  it('builds a valid identity-only query for an empty catalog without unused range variables', () => {
    const built = buildTimelineQuery(
      validateTimelineCatalog({ version: 1, schemaHash: 'b'.repeat(64), sources: [] }),
    );
    const query = print(built.document);

    expect(query).toContain('query DeviceTimeline($id: ID!)');
    expect(query).not.toContain('$since');
    expect(query).not.toContain('$until');
    expect(built.fields).toEqual([]);
  });

  it('rejects unsupported and unsafe metadata with explicit messages', () => {
    expect(() => validateTimelineCatalog({ ...FOUR_SOURCES, version: 2 })).toThrow(
      'Unsupported timeline catalog version 2',
    );
    expect(() =>
      validateTimelineCatalog({
        ...FOUR_SOURCES,
        sources: [{ ...FOUR_SOURCES.sources[0], contractVersion: 2 }],
      }),
    ).toThrow('unsupported contractVersion 2');
    expect(() =>
      validateTimelineCatalog({
        ...FOUR_SOURCES,
        sources: [{ ...FOUR_SOURCES.sources[0], field: 'patchTimeline } mutation Inject {' }],
      }),
    ).toThrow('invalid GraphQL field');
    expect(() =>
      validateTimelineCatalog({
        ...FOUR_SOURCES,
        sources: [FOUR_SOURCES.sources[0], { ...FOUR_SOURCES.sources[0] }],
      }),
    ).toThrow('duplicate source id');
  });
});
