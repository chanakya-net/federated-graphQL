import { TestBed } from '@angular/core/testing';

import { FOURTH_SOURCE, wireEvent } from '../../../testing/timeline-test-data';
import type { TimelineEvent } from '../../timeline/timeline.models';
import { EventDetailComponent } from './event-detail.component';

const event: TimelineEvent = {
  ...wireEvent('cert-1', {
    title: 'Certificate issued',
    subtitle: 'workstation.example',
    status: 'ISSUED',
    severity: 'MEDIUM',
    details: [
      { label: 'Serial', value: '01:ab', mono: true },
      { label: 'Serial', value: 'friendly serial', mono: false },
      { label: 'Issuer', value: 'Example CA', mono: false },
    ],
  }),
  source: FOURTH_SOURCE.id,
  sourceMeta: FOURTH_SOURCE,
};

async function render(value: TimelineEvent | null) {
  const fixture = TestBed.createComponent(EventDetailComponent);
  fixture.componentRef.setInput('event', value);
  await fixture.whenStable();
  return { fixture, el: fixture.nativeElement as HTMLElement };
}

describe('EventDetailComponent', () => {
  it('renders a new source entirely from metadata and uniform details', async () => {
    const { el } = await render(event);
    expect(el.dataset['source']).toBe(FOURTH_SOURCE.id);
    expect(el.querySelector('.source')?.textContent).toContain(FOURTH_SOURCE.name);
    expect(el.querySelector('.title')?.textContent).toBe(event.title);
    expect([...el.querySelectorAll('.chip')].map((chip) => chip.textContent?.trim())).toEqual([
      'ISSUED',
      'MEDIUM',
    ]);
    const fields = [...el.querySelectorAll('.field')].map((field) => [
      field.querySelector('dt')?.textContent?.trim(),
      field.querySelector('dd')?.textContent?.trim(),
      field.querySelector('dd')?.classList.contains('mono'),
    ]);
    expect(fields).toEqual([
      ['Serial', '01:ab', true],
      ['Serial', 'friendly serial', false],
      ['Issuer', 'Example CA', false],
    ]);
  });

  it('shows a hint for no selection and emits close', async () => {
    const empty = await render(null);
    expect(empty.el.querySelector('.hint')).not.toBeNull();
    const selected = await render(event);
    let closed = 0;
    selected.fixture.componentInstance.close.subscribe(() => closed++);
    selected.el.querySelector<HTMLButtonElement>('button.close')!.click();
    expect(closed).toBe(1);
  });
});
