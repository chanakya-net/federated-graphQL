import { TestBed } from '@angular/core/testing';

import { FOURTH_SOURCE, TIMELINE_SOURCES } from '../../../testing/timeline-test-data';
import type { SectionState } from '../../timeline/section-state';
import type { TimelineSource } from '../../timeline/timeline-catalog';
import { SectionBannerComponent } from './section-banner.component';

async function render(source: TimelineSource, state: SectionState<unknown> | null) {
  const fixture = TestBed.createComponent(SectionBannerComponent);
  fixture.componentRef.setInput('section', source);
  fixture.componentRef.setInput('state', state);
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

describe('SectionBannerComponent', () => {
  it('renders catalog presentation for a new source and its event count', async () => {
    const el = await render(FOURTH_SOURCE, { kind: 'ok', events: [1, 2] });
    expect(el.dataset['section']).toBe(FOURTH_SOURCE.id);
    expect(el.style.getPropertyValue('--source-color')).toBe(FOURTH_SOURCE.color);
    expect(el.querySelector('mat-icon')?.textContent?.trim()).toBe(FOURTH_SOURCE.icon);
    expect(el.querySelector('.name')?.textContent?.trim()).toBe(FOURTH_SOURCE.name);
    expect(el.querySelector('.count')?.textContent?.trim()).toBe('2 events');
  });

  it('derives denied and unavailable copy from metadata', async () => {
    const denied = await render(FOURTH_SOURCE, { kind: 'no-access' });
    expect(denied.textContent).toContain("You don't have access to Certificate data.");
    const unavailable = await render(FOURTH_SOURCE, {
      kind: 'unavailable',
      message: 'timeout',
    });
    expect(unavailable.textContent).toContain(
      'Certificate service is currently unavailable — certificate history is not shown.',
    );
    expect(unavailable.textContent).toContain('Gateway error: timeout');
  });

  it('shows metadata while loading', async () => {
    const el = await render(TIMELINE_SOURCES[0], null);
    expect(el.classList).toContain('loading');
    expect(el.querySelector('[aria-busy="true"]')).not.toBeNull();
  });
});
