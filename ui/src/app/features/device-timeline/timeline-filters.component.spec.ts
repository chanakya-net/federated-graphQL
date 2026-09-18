import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNativeDateAdapter } from '@angular/material/core';

import { DEFAULT_FILTER, type TimelineFilter } from '../../timeline/timeline.models';
import { TimelineFiltersComponent, endOfDayIso, startOfDayIso } from './timeline-filters.component';

@Component({
  imports: [TimelineFiltersComponent],
  template: `<app-timeline-filters [(filter)]="filter" />`,
})
class Host {
  readonly filter = signal<TimelineFilter>(DEFAULT_FILTER);
}

describe('TimelineFiltersComponent', () => {
  let host: Host;
  let el: HTMLElement;
  let changes: TimelineFilter[];

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideNativeDateAdapter()] });
    const fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
    changes = [];
    const set = host.filter.set.bind(host.filter);
    host.filter.set = (value: TimelineFilter) => {
      changes.push(value);
      set(value);
    };
  });

  function type(input: HTMLInputElement, value: string, commit = false): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    if (commit) input.dispatchEvent(new Event('change'));
  }

  const start = () => el.querySelector<HTMLInputElement>('input[placeholder="From"]')!;
  const end = () => el.querySelector<HTMLInputElement>('input[placeholder="To"]')!;

  it('a typed date range applies once, when committed, as local-day bounds', () => {
    type(start(), '8/1/2026', true);
    expect(changes).toHaveLength(0); // start alone: nothing to apply yet
    type(end(), '8'); // already parses as a date: keystrokes must not query
    type(end(), '8/31');
    type(end(), '8/31/2026');
    expect(changes).toHaveLength(0);

    end().dispatchEvent(new Event('change'));
    expect(changes).toHaveLength(1);
    expect(host.filter().since).toBe(startOfDayIso(new Date(2026, 7, 1)));
    expect(host.filter().until).toBe(endOfDayIso(new Date(2026, 7, 31)));
  });

  it('an inverted range is not applied', () => {
    type(start(), '8/31/2026', true);
    type(end(), '8/1/2026', true);
    expect(host.filter().since).toBeUndefined();
    expect(changes).toHaveLength(0);
  });

  it('text, sources and statuses update the filter', () => {
    const text = el.querySelector<HTMLInputElement>('.text input')!;
    type(text, 'python');
    expect(host.filter().text).toBe('python');

    el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button')[1].click();
    expect([...host.filter().sources]).toEqual(['patch', 'softwareinstall']);
  });

  it('day bounds cover the whole local day', () => {
    const day = new Date(2026, 7, 15, 13, 45);
    expect(Date.parse(endOfDayIso(day)) - Date.parse(startOfDayIso(day))).toBe(
      24 * 3600 * 1000 - 1,
    );
    expect(new Date(startOfDayIso(day)).getHours()).toBe(0);
  });
});
