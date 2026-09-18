import { formatDate } from '@angular/common';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import fullAlice from '../../../testing/fixtures/gateway/timeline-full-alice.json';
import type { InstallEvent, PatchEvent, VulnerabilityEvent } from '../../graphql/types';
import { merge } from '../../timeline/timeline-merge';
import { eventKey, type TimelineEvent } from '../../timeline/timeline.models';
import { TimelineStripComponent } from './timeline-strip.component';

@Component({
  imports: [TimelineStripComponent],
  template: `
    <app-timeline-strip
      [events]="events()"
      [total]="total()"
      [loading]="loading()"
      [(selected)]="selected"
    />
  `,
})
class Host {
  readonly events = signal<TimelineEvent[]>([]);
  readonly total = signal(0);
  readonly loading = signal(false);
  readonly selected = signal<string | null>(null);
}

const device = fullAlice.data.device as unknown as {
  patchEvents: PatchEvent[];
  vulnerabilityEvents: VulnerabilityEvent[];
  installEvents: InstallEvent[];
};
/** Newest first, as the page holds them. */
const events = merge({
  patchEvents: { kind: 'ok', events: device.patchEvents },
  vulnerabilityEvents: { kind: 'ok', events: device.vulnerabilityEvents },
  installEvents: { kind: 'ok', events: device.installEvents },
});
const newest = events[0];
const oldest = events.at(-1)!;

describe('TimelineStripComponent', () => {
  let host: Host;
  let el: HTMLElement;
  let stable: () => Promise<void>;

  beforeEach(async () => {
    const fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    el = fixture.nativeElement as HTMLElement;
    stable = () => fixture.whenStable();
    host.events.set(events);
    host.total.set(events.length);
    await stable();
  });

  const points = () => [...el.querySelectorAll<HTMLButtonElement>('.point-button')];
  const datetime = (b: Element) => b.querySelector('time')!.getAttribute('datetime')!;

  it('one point per event, oldest on the left, each in its subgraph colour', () => {
    expect(points()).toHaveLength(events.length);
    const times = points().map((b) => Date.parse(datetime(b)));
    expect(times).toEqual([...times].sort((a, b) => a - b));
    expect(points()[0].dataset['key']).toBe(eventKey(oldest));
    expect(points().at(-1)!.dataset['key']).toBe(eventKey(newest));

    const colours = new Map(
      [...el.querySelectorAll<HTMLElement>('li.point')].map((li) => [
        li.dataset['source'],
        li.style.getPropertyValue('--source-color'),
      ]),
    );
    expect(colours).toEqual(
      new Map([
        ['patch', '#1d4ed8'],
        ['vulnerability', '#be185d'],
        ['softwareinstall', '#047857'],
      ]),
    );
    // The track stops at the first and last point only.
    const lis = [...el.querySelectorAll('li.point')];
    expect(lis.filter((li) => li.classList.contains('first'))).toEqual([lis[0]]);
    expect(lis.filter((li) => li.classList.contains('last'))).toEqual([lis.at(-1)!]);
  });

  it('groups the points by month, in order, each month once', () => {
    const labels = [...el.querySelectorAll('.month-label')].map((l) => l.textContent!.trim());
    const expected = [...new Set(events.map((e) => formatDate(e.occurredAt, 'MMM y', 'en-US')))];
    expect(labels).toEqual(expected.reverse());
    const perMonth = [...el.querySelectorAll('.month')].map(
      (m) => m.querySelectorAll('.point').length,
    );
    expect(perMonth.reduce((a, b) => a + b, 0)).toBe(events.length);
  });

  it('a point shows the short label and the day; its name has the date, source and title', () => {
    const last = points().at(-1)!;
    expect(last.querySelector('.label')!.textContent).toBe(newest.label);
    expect(last.querySelector('.when')!.textContent!.trim()).toBe(
      formatDate(newest.occurredAt, 'MMM d', 'en-US'),
    );
    expect(last.getAttribute('aria-label')).toBe(
      `${formatDate(newest.occurredAt, 'medium', 'en-US')} · Vulnerability · ${newest.title}`,
    );
  });

  it('click selects (two-way), a second click clears', async () => {
    const last = points().at(-1)!;
    expect(last.getAttribute('aria-pressed')).toBe('false');
    last.click();
    await stable();
    expect(host.selected()).toBe(eventKey(newest));
    expect(last.classList).toContain('selected');
    expect(last.getAttribute('aria-pressed')).toBe('true');
    expect(el.querySelectorAll('.point-button.selected')).toHaveLength(1);

    last.click();
    await stable();
    expect(host.selected()).toBeNull();
    expect(el.querySelector('.point-button.selected')).toBeNull();
  });

  it('a selection made elsewhere highlights the point', async () => {
    host.selected.set(eventKey(oldest));
    await stable();
    expect(points()[0].classList).toContain('selected');
    host.selected.set('patch:no-such-event');
    await stable();
    expect(el.querySelector('.point-button.selected')).toBeNull();
  });

  it('arrow keys move the focus between points; Home and End jump', async () => {
    const all = points();
    all[3].focus();
    const key = (name: string, from: Element) =>
      from.dispatchEvent(
        new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }),
      );
    key('ArrowRight', all[3]);
    expect(document.activeElement).toBe(all[4]);
    key('ArrowLeft', all[4]);
    key('ArrowLeft', all[3]);
    expect(document.activeElement).toBe(all[2]);
    key('End', all[2]);
    expect(document.activeElement).toBe(all.at(-1));
    key('ArrowRight', all.at(-1)!); // stays at the end
    expect(document.activeElement).toBe(all.at(-1));
    key('Home', all.at(-1)!);
    expect(document.activeElement).toBe(all[0]);
  });

  it('empty: says whether the filters or the sections hide everything', async () => {
    host.events.set([]);
    await stable();
    expect(el.querySelector('.empty')!.textContent!.trim()).toBe('No events match the filters.');
    expect(el.querySelector('.point')).toBeNull();
    host.total.set(0);
    await stable();
    expect(el.querySelector('.empty')!.textContent!.trim()).toBe(
      'No events in the sections shown.',
    );
  });

  it('loading: a skeleton track, no points', async () => {
    host.loading.set(true);
    await stable();
    expect(el.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(el.querySelector('.point')).toBeNull();
  });
});
