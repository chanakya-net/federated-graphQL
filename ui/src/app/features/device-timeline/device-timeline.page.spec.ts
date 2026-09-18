import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNativeDateAdapter } from '@angular/material/core';
import { provideRouter } from '@angular/router';

import crossTenant from '../../../testing/fixtures/gateway/timeline-cross-tenant-dave.json';
import deniedBob from '../../../testing/fixtures/gateway/timeline-denied-bob.json';
import directoryDown from '../../../testing/fixtures/gateway/timeline-directory-down.json';
import fullAlice from '../../../testing/fixtures/gateway/timeline-full-alice.json';
import outageStop from '../../../testing/fixtures/gateway/timeline-outage-stop-patch.json';
import rangeAlice from '../../../testing/fixtures/gateway/timeline-range-alice.json';
import devUsers from '../../../testing/tokens.dev.json';
import { authInterceptor } from '../../core/auth.interceptor';
import type { DemoUser } from '../../core/demo-user';
import { provideGraphql } from '../../core/graphql.provider';
import { SELECTED_USER_KEY, SessionService } from '../../core/session.service';
import { DeviceTimelinePage } from './device-timeline.page';

// The real Apollo client (provideGraphql: errorPolicy 'all', HttpLink, InMemoryCache) and the real
// interceptor, with only the HTTP backend replaced: recorded gateway bodies go in, the DOM comes out.

const users = devUsers as DemoUser[];
const user = (sub: string) => users.find((u) => u.sub === sub)!;

describe('DeviceTimelinePage', () => {
  let backend: HttpTestingController;
  let session: SessionService;
  let fixture: ComponentFixture<DeviceTimelinePage>;
  let el: HTMLElement;

  async function settle(): Promise<void> {
    for (let i = 0; i < 5; i++) {
      TestBed.tick();
      await new Promise((resolve) => setTimeout(resolve));
    }
  }

  async function open(as: string, id = 'dev-00001'): Promise<TestRequest> {
    localStorage.setItem(SELECTED_USER_KEY, as);
    const loading = session.load();
    backend.expectOne('/tokens.json').flush(users);
    await loading;
    fixture = TestBed.createComponent(DeviceTimelinePage);
    fixture.componentRef.setInput('id', id);
    el = fixture.nativeElement as HTMLElement;
    await settle();
    return backend.expectOne('/graphql');
  }

  async function respond(req: TestRequest, body: object): Promise<void> {
    req.flush(body);
    await settle();
  }

  const banners = (kind: string) => [...el.querySelectorAll(`app-section-banner.${kind}`)];
  const sectionOf = (banner: Element) => banner.getAttribute('data-section');
  const listItems = () => [...el.querySelectorAll('app-timeline-list li.event')];
  const stateCard = () =>
    el.querySelector('app-state-card')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideGraphql(),
        provideNativeDateAdapter(),
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
  });

  afterEach(() => {
    backend.verify();
    localStorage.clear();
  });

  it('always requests all three sections, with the selected user token', async () => {
    const req = await open('bob');
    expect(req.request.headers.get('Authorization')).toBe(`Bearer ${user('bob').token}`);
    expect(req.request.body.operationName).toBe('DeviceTimeline');
    expect(req.request.body.variables).toEqual({ id: 'dev-00001', since: null, until: null });
    for (const field of ['patchEvents', 'vulnerabilityEvents', 'installEvents']) {
      expect(req.request.body.query).toContain(field);
    }
    await respond(req, deniedBob);
  });

  it('shows skeletons, never a blank page, while loading', async () => {
    const req = await open('alice');
    expect(el.querySelector('mat-progress-bar')).not.toBeNull();
    expect(el.querySelectorAll('app-section-banner.loading')).toHaveLength(3);
    expect(el.querySelector('app-timeline-list [aria-busy="true"]')).not.toBeNull();
    await respond(req, fullAlice);
    expect(el.querySelector('mat-progress-bar')).toBeNull();
  });

  it('partial data reaches the page (errorPolicy all): two sections plus one lock banner for bob', async () => {
    await respond(await open('bob'), deniedBob);

    expect(banners('ok').map(sectionOf)).toEqual(['patchEvents', 'vulnerabilityEvents']);
    expect(banners('no-access').map(sectionOf)).toEqual(['installEvents']);
    expect(banners('unavailable')).toHaveLength(0);
    expect(banners('no-access')[0].querySelector('mat-icon')?.textContent?.trim()).toBe('lock');
    expect(banners('no-access')[0].querySelector('.copy')?.textContent).toBe(
      "You don't have access to Software Install data.",
    );

    const device = deniedBob.data.device;
    expect(el.querySelector('mat-card-title')?.textContent?.trim()).toBe(device.hostname);
    expect(listItems()).toHaveLength(device.patchEvents.length + device.vulnerabilityEvents.length);
    expect(listItems().some((li) => li.getAttribute('data-source') === 'softwareinstall')).toBe(
      false,
    );
  });

  it('an outage shows the warning banner for that section only', async () => {
    await respond(await open('alice'), outageStop);
    expect(banners('unavailable').map(sectionOf)).toEqual(['patchEvents']);
    expect(banners('unavailable')[0].querySelector('mat-icon')?.textContent?.trim()).toBe(
      'warning',
    );
    expect(banners('unavailable')[0].querySelector('.copy')?.textContent).toBe(
      'Patch service is currently unavailable — patch history is not shown.',
    );
    expect(banners('ok').map(sectionOf)).toEqual(['vulnerabilityEvents', 'installEvents']);
    expect(banners('no-access')).toHaveLength(0);
  });

  it('merges all three sections newest first for alice', async () => {
    await respond(await open('alice'), fullAlice);
    const d = fullAlice.data.device;
    expect(banners('ok')).toHaveLength(3);
    expect(listItems()).toHaveLength(
      d.patchEvents.length + d.vulnerabilityEvents.length + d.installEvents.length,
    );
    const times = listItems().map((li) =>
      Date.parse(li.querySelector('time')!.getAttribute('datetime')!),
    );
    expect(times).toEqual([...times].sort((a, b) => b - a));
  });

  it('device null without errors: not found in the user tenant', async () => {
    await respond(await open('dave'), crossTenant);
    expect(stateCard()).toContain('Device dev-00001 not found in TenantB');
    expect(el.querySelector('app-section-banner')).toBeNull();
  });

  it('device null with errors: device directory unavailable', async () => {
    await respond(await open('alice'), directoryDown);
    expect(stateCard()).toContain('Device directory unavailable');
    expect(stateCard()).toContain('Gateway error: Unexpected Execution Error');
  });

  it('HTTP 401: a global error with a way out', async () => {
    const req = await open('alice');
    req.flush('', { status: 401, statusText: 'Unauthorized' });
    await settle();
    expect(stateCard()).toContain('Not authenticated (HTTP 401)');
    expect(el.querySelector('app-state-card button')?.textContent).toContain('Reload users');
  });

  it('switching the user re-runs the query with the new token', async () => {
    await respond(await open('alice'), fullAlice);
    await session.select('dave');
    await settle();
    const req = backend.expectOne('/graphql');
    expect(req.request.headers.get('Authorization')).toBe(`Bearer ${user('dave').token}`);
    await respond(req, crossTenant);
    expect(stateCard()).toContain('not found in TenantB');
  });

  it('the date range goes to the server; the other filters stay client-side', async () => {
    await respond(await open('alice'), fullAlice);
    const page = fixture.componentInstance as unknown as {
      filter: { update: (fn: (f: object) => object) => void };
    };

    page.filter.update((f) => ({ ...f, text: 'python' }));
    await settle();
    backend.expectNone('/graphql');
    expect(listItems().every((li) => li.textContent!.toLowerCase().includes('python'))).toBe(true);

    page.filter.update((f) => ({
      ...f,
      text: '',
      since: '2026-08-01T00:00:00.000Z',
      until: '2026-08-31T23:59:59.999Z',
    }));
    await settle();
    const req = backend.expectOne('/graphql');
    expect(req.request.body.variables).toEqual({
      id: 'dev-00001',
      since: '2026-08-01T00:00:00.000Z',
      until: '2026-08-31T23:59:59.999Z',
    });
    await respond(req, rangeAlice);
    expect(banners('ok')).toHaveLength(3);
    expect(banners('ok')[0].textContent).toContain('0 events'); // patchEvents: [] is ok, not degraded
  });
});
