import { TestBed } from '@angular/core/testing';

import type { SectionState } from '../../timeline/section-state';
import { SECTIONS, type SectionMeta } from '../../timeline/timeline.models';
import { SectionBannerComponent } from './section-banner.component';

// Plan §7, verbatim.
const COPY = {
  Patch: {
    noAccess: "You don't have access to Patch data.",
    unavailable: 'Patch service is currently unavailable — patch history is not shown.',
  },
  Vulnerability: {
    noAccess: "You don't have access to Vulnerability data.",
    unavailable:
      'Vulnerability service is currently unavailable — vulnerability history is not shown.',
  },
  'Software Install': {
    noAccess: "You don't have access to Software Install data.",
    unavailable:
      'Software Install service is currently unavailable — install history is not shown.',
  },
} as const;

async function render(
  section: SectionMeta,
  state: SectionState<unknown> | null,
): Promise<HTMLElement> {
  const fixture = TestBed.createComponent(SectionBannerComponent);
  fixture.componentRef.setInput('section', section);
  fixture.componentRef.setInput('state', state);
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

const icon = (el: HTMLElement) => el.querySelector('mat-icon')?.textContent?.trim();
const part = (el: HTMLElement, selector: string) => el.querySelector(selector)?.textContent?.trim();

describe('SectionBannerComponent', () => {
  for (const section of SECTIONS) {
    const copy = COPY[section.name as keyof typeof COPY];

    it(`${section.name}: no-access shows the lock and the exact copy`, async () => {
      const el = await render(section, { kind: 'no-access' });
      expect(el.classList).toContain('no-access');
      expect(icon(el)).toBe('lock');
      expect(el.querySelector('.copy')?.textContent).toBe(copy.noAccess);
      expect(el.querySelector('[role="status"]')).not.toBeNull();
    });

    it(`${section.name}: unavailable shows the warning, the exact copy and the gateway message`, async () => {
      const el = await render(section, {
        kind: 'unavailable',
        message: 'Unexpected Execution Error',
      });
      expect(el.classList).toContain('unavailable');
      expect(icon(el)).toBe('warning');
      expect(el.querySelector('.copy')?.textContent).toBe(copy.unavailable);
      expect(el.querySelector('.detail')?.textContent).toBe(
        'Gateway error: Unexpected Execution Error',
      );
      expect(el.querySelector('[role="alert"]')).not.toBeNull();
    });
  }

  it('ok shows the section name and event count, no banner', async () => {
    const el = await render(SECTIONS[0], { kind: 'ok', events: [1, 2, 3] });
    expect(el.classList).toContain('ok');
    expect(icon(el)).toBe(SECTIONS[0].icon);
    expect(part(el, '.name')).toBe('Patch');
    expect(part(el, '.count')).toBe('3 events');
    expect(el.querySelector('.banner')).toBeNull();
  });

  it('ok with no events says 0 events', async () => {
    const el = await render(SECTIONS[2], { kind: 'ok', events: [] });
    expect(part(el, '.name')).toBe('Software Install');
    expect(part(el, '.count')).toBe('0 events');
  });

  it('null is loading', async () => {
    const el = await render(SECTIONS[1], null);
    expect(el.classList).toContain('loading');
    expect(el.querySelector('[aria-busy="true"]')).not.toBeNull();
  });
});
