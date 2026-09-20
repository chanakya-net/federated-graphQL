import { FOURTH_SOURCE, TIMELINE_SOURCES, wireEvent } from '../../testing/timeline-test-data';
import type { TimelineSections } from './section-state';
import { applyFilter, merge } from './timeline-merge';
import { defaultTimelineFilter, eventKey, type TimelineEvent } from './timeline.models';

const event = (
  source = TIMELINE_SOURCES[0],
  id = 'event-1',
  change: Partial<TimelineEvent> = {},
): TimelineEvent => ({ ...wireEvent(id), source: source.id, sourceMeta: source, ...change });

describe('generic timeline merge and filter', () => {
  it('merges any catalog source, leaves failed sources out, and sorts newest first', () => {
    const sections: TimelineSections = {
      patch: { kind: 'no-access' },
      vulnerability: {
        kind: 'ok',
        events: [event(TIMELINE_SOURCES[1], 'v', { occurredAt: '2026-08-20T00:00:00Z' })],
      },
      'certificate history': {
        kind: 'ok',
        events: [event(FOURTH_SOURCE, 'cert', { occurredAt: '2026-09-01T00:00:00Z' })],
      },
    };

    expect(merge(sections).map((item) => item.id)).toEqual(['cert', 'v']);
  });

  it('uses source-aware keys without separator collisions', () => {
    expect(eventKey(event(FOURTH_SOURCE, 'a:b'))).not.toBe(
      eventKey(event({ ...FOURTH_SOURCE, id: 'certificate history:a' }, 'b')),
    );
  });

  it('filters an unknown fourth source by source, status, range and text', () => {
    const cert = event(FOURTH_SOURCE, 'cert', {
      occurredAt: '2026-08-10T00:00:00Z',
      status: 'EXPIRED',
      title: 'Expired workstation certificate',
    });
    const patch = event(TIMELINE_SOURCES[0], 'patch', { status: 'FAILED' });
    const filter = {
      ...defaultTimelineFilter([...TIMELINE_SOURCES, FOURTH_SOURCE]),
      sources: new Set([FOURTH_SOURCE.id]),
      statuses: new Set(['EXPIRED']),
      since: '2026-08-01T00:00:00Z',
      until: '2026-08-31T23:59:59Z',
      text: 'WORKSTATION',
    };

    expect(applyFilter([patch, cert], filter)).toEqual([cert]);
  });
});
