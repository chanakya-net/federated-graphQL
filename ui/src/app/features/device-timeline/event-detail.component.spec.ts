import { TestBed } from '@angular/core/testing';

import fullAlice from '../../../testing/fixtures/gateway/timeline-full-alice.json';
import type { InstallEvent, PatchEvent, VulnerabilityEvent } from '../../graphql/types';
import { fromInstall, fromPatch, fromVulnerability } from '../../timeline/timeline-merge';
import type { TimelineEvent } from '../../timeline/timeline.models';
import { EventDetailComponent } from './event-detail.component';

const device = fullAlice.data.device as unknown as {
  patchEvents: PatchEvent[];
  vulnerabilityEvents: VulnerabilityEvent[];
  installEvents: InstallEvent[];
};

async function render(event: TimelineEvent | null) {
  const fixture = TestBed.createComponent(EventDetailComponent);
  fixture.componentRef.setInput('event', event);
  await fixture.whenStable();
  return { fixture, el: fixture.nativeElement as HTMLElement };
}

/** `dt` -> `dd` of the fields grid. */
const fields = (el: HTMLElement) =>
  new Map(
    [...el.querySelectorAll('.field')].map((f) => [
      f.querySelector('dt')!.textContent!.trim(),
      f.querySelector('dd')!.textContent!.trim(),
    ]),
  );
const chips = (el: HTMLElement) =>
  [...el.querySelectorAll('.chip')].map((c) => c.textContent!.trim());

describe('EventDetailComponent', () => {
  it('patch: source, title, status and severity chips, every patch field', async () => {
    const p = device.patchEvents[0];
    const { el } = await render(fromPatch(p));
    expect(el.dataset['source']).toBe('patch');
    expect(el.querySelector('.source')!.textContent).toContain('Patch');
    expect(el.querySelector('.title')!.textContent).toBe(p.patch.title);
    expect(el.querySelector('time')!.getAttribute('datetime')).toBe(p.occurredAt);
    expect(chips(el)).toEqual([p.status, p.patch.severity]);
    expect(fields(el)).toEqual(
      new Map([
        ['KB', p.patch.kbId],
        ['Vendor', p.patch.vendor],
        ['Severity', p.patch.severity],
        ['Status', p.status],
        ['Patch ID', p.patch.id],
        ['Event ID', p.id],
      ]),
    );
  });

  it('vulnerability: CVE, CVSS score to one decimal, kind, finding state and ids', async () => {
    const v = device.vulnerabilityEvents[0];
    const { el } = await render(fromVulnerability(v));
    expect(el.querySelector('.source')!.textContent).toContain('Vulnerability');
    expect(el.querySelector('.title')!.textContent).toBe(`${v.kind} ${v.cve.id}`);
    expect(el.querySelector('.subtitle')!.textContent).toBe(v.cve.title);
    expect(fields(el)).toEqual(
      new Map([
        ['CVE', v.cve.id],
        ['CVSS score', v.cve.cvssScore.toFixed(1)],
        ['Severity', v.cve.severity],
        ['Event', v.kind],
        ['Finding state', v.findingState],
        ['Finding ID', v.findingId],
        ['Event ID', v.id],
      ]),
    );
  });

  it('install: action, software, version, publisher, result; no severity chip', async () => {
    const i = device.installEvents[0];
    const { el } = await render(fromInstall(i));
    expect(el.querySelector('.source')!.textContent).toContain('Software Install');
    expect(chips(el)).toEqual([i.result]);
    expect(fields(el)).toEqual(
      new Map([
        ['Action', i.action],
        ['Software', i.software.name],
        ['Version', i.software.version],
        ['Publisher', i.software.publisher],
        ['Result', i.result],
        ['Event ID', i.id],
      ]),
    );
  });

  it('nothing selected: a hint, no details', async () => {
    const { el } = await render(null);
    expect(el.querySelector('.detail')).toBeNull();
    expect(el.querySelector('.hint')!.textContent).toContain('Select a point on the timeline');
    expect(el.dataset['source']).toBeUndefined();
  });

  it('close emits', async () => {
    const { fixture, el } = await render(fromPatch(device.patchEvents[0]));
    let closed = 0;
    fixture.componentInstance.close.subscribe(() => closed++);
    el.querySelector<HTMLButtonElement>('button.close')!.click();
    expect(closed).toBe(1);
  });
});
