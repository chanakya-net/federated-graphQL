import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { FOURTH_SOURCE, TIMELINE_SOURCES, wireEvent } from '../../../testing/timeline-test-data';
import { eventKey, type TimelineEvent } from '../../timeline/timeline.models';
import { TimelineStripComponent } from './timeline-strip.component';

const events: TimelineEvent[] = [
  {
    ...wireEvent('cert', { occurredAt: '2026-09-01T00:00:00Z' }),
    source: FOURTH_SOURCE.id,
    sourceMeta: FOURTH_SOURCE,
  },
  {
    ...wireEvent('patch', { occurredAt: '2026-08-01T00:00:00Z' }),
    source: TIMELINE_SOURCES[0].id,
    sourceMeta: TIMELINE_SOURCES[0],
  },
];

@Component({
  imports: [TimelineStripComponent],
  template: `<app-timeline-strip
    [events]="events"
    [total]="events.length"
    [(selected)]="selected"
  />`,
})
class Host {
  readonly events = events;
  readonly selected = signal<string | null>(null);
}

describe('TimelineStripComponent', () => {
  it('orders points oldest-first and uses each event catalog metadata', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const points = [...el.querySelectorAll<HTMLButtonElement>('.point-button')];
    expect(points.map((point) => point.dataset['key'])).toEqual([
      eventKey(events[1]),
      eventKey(events[0]),
    ]);
    const fourth = points[1].closest<HTMLElement>('.point')!;
    expect(fourth.dataset['source']).toBe(FOURTH_SOURCE.id);
    expect(fourth.style.getPropertyValue('--source-color')).toBe(FOURTH_SOURCE.color);
    expect(points[1].getAttribute('aria-label')).toContain(`${FOURTH_SOURCE.name} ·`);
    points[1].click();
    await fixture.whenStable();
    expect(fixture.componentInstance.selected()).toBe(eventKey(events[0]));
  });
});
