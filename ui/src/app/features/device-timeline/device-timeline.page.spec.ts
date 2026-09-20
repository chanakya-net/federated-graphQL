import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNativeDateAdapter } from '@angular/material/core';
import { provideRouter } from '@angular/router';

import devUsers from '../../../testing/tokens.dev.json';
import {
  FOURTH_SOURCE,
  TIMELINE_SOURCES,
  catalog,
  timelineBody,
  wireEvent,
} from '../../../testing/timeline-test-data';
import { authInterceptor } from '../../core/auth.interceptor';
import type { DemoUser } from '../../core/demo-user';
import { provideGraphql } from '../../core/graphql.provider';
import { SELECTED_USER_KEY, SessionService } from '../../core/session.service';
import type { TimelineFilter } from '../../timeline/timeline.models';
import { DeviceTimelinePage } from './device-timeline.page';

const users = devUsers as DemoUser[];
const user = (sub: string) => users.find((candidate) => candidate.sub === sub)!;

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

  async function begin(as = 'alice', id = 'dev-00001'): Promise<TestRequest> {
    localStorage.setItem(SELECTED_USER_KEY, as);
    const loading = session.load();
    backend.expectOne('/tokens.json').flush(users);
    await loading;
    fixture = TestBed.createComponent(DeviceTimelinePage);
    fixture.componentRef.setInput('id', id);
    el = fixture.nativeElement as HTMLElement;
    await settle();
    return backend.expectOne('/timeline-sources');
  }

  async function open(sourceCatalog = catalog(), as = 'alice'): Promise<TestRequest> {
    const metadata = await begin(as);
    expect(metadata.request.headers.get('Authorization')).toBeNull();
    metadata.flush(sourceCatalog);
    await settle();
    return backend.expectOne('/graphql');
  }

  async function respond(req: TestRequest, body: object): Promise<void> {
    req.flush(body);
    await settle();
  }

  const stateCard = () =>
    el.querySelector('app-state-card')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const banners = (kind: string) => [...el.querySelectorAll(`app-section-banner.${kind}`)];
  const rows = () => [...el.querySelectorAll<HTMLElement>('app-timeline-list li.event')];

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

  it('queries, renders, filters and details a fourth source while preserving a partial failure', async () => {
    const sources = [...TIMELINE_SOURCES, FOURTH_SOURCE];
    const req = await open(catalog(sources));
    expect(req.request.headers.get('Authorization')).toBe(`Bearer ${user('alice').token}`);
    expect(req.request.body.operationName).toBe('DeviceTimeline');
    expect(req.request.body.variables).toEqual({ id: 'dev-00001', since: null, until: null });
    for (const [index, source] of sources.entries()) {
      expect(req.request.body.query).toContain(`timelineSource${index}: ${source.field}`);
    }
    expect(req.request.body.query).toContain('details');
    expect(req.request.body.query).not.toContain(FOURTH_SOURCE.id);

    await respond(
      req,
      timelineBody(
        sources,
        {
          vulnerability: [wireEvent('vuln', { status: 'OPEN' })],
          softwareinstall: [],
          [FOURTH_SOURCE.id]: [
            wireEvent('cert-1', {
              occurredAt: '2026-08-30T12:00:00Z',
              label: 'workstation.example',
              title: 'Certificate issued',
              status: 'ISSUED',
              details: [{ label: 'Serial', value: '01:ab', mono: true }],
            }),
          ],
        },
        {
          patch: {
            message: 'The current user is not authorized to access this resource.',
            code: 'AUTH_NOT_AUTHORIZED',
          },
        },
      ),
    );

    expect(banners('no-access').map((banner) => banner.getAttribute('data-section'))).toEqual([
      'patch',
    ]);
    expect(banners('ok').map((banner) => banner.getAttribute('data-section'))).toEqual([
      'vulnerability',
      'softwareinstall',
      FOURTH_SOURCE.id,
    ]);
    const certRow = rows().find((row) => row.dataset['source'] === FOURTH_SOURCE.id)!;
    expect(certRow.textContent).toContain('Certificate issued');
    expect(el.textContent).toContain(FOURTH_SOURCE.name);

    const page = fixture.componentInstance as unknown as {
      filter: { update: (fn: (filter: TimelineFilter) => TimelineFilter) => void };
    };
    page.filter.update((filter) => ({
      ...filter,
      sources: new Set([FOURTH_SOURCE.id]),
      statuses: new Set(['ISSUED']),
    }));
    await settle();
    expect(rows()).toHaveLength(1);
    rows()[0].querySelector<HTMLButtonElement>('button.row')!.click();
    await settle();
    const detail = el.querySelector('app-event-detail .detail')!;
    expect(detail.textContent).toContain('Certificate issued');
    expect(detail.textContent).toContain('Serial');
    expect(detail.querySelector('dd')?.classList).toContain('mono');

    page.filter.update((filter) => ({
      ...filter,
      since: '2026-08-01T00:00:00.000Z',
      until: '2026-08-31T23:59:59.999Z',
    }));
    await settle();
    backend.expectNone('/timeline-sources');
    const ranged = backend.expectOne('/graphql');
    expect(ranged.request.body.variables).toEqual({
      id: 'dev-00001',
      since: '2026-08-01T00:00:00.000Z',
      until: '2026-08-31T23:59:59.999Z',
    });
    expect(ranged.request.body.query).toContain('certificateTimeline');
    await respond(ranged, timelineBody(sources, { [FOURTH_SOURCE.id]: [] }));
  });

  it('refreshes metadata for user changes and rebuilds queries after source addition and removal', async () => {
    await respond(await open(), timelineBody(TIMELINE_SOURCES, {}));

    const expanded = [...TIMELINE_SOURCES, FOURTH_SOURCE];
    await session.select('bob');
    await settle();
    backend.expectOne('/timeline-sources').flush(catalog(expanded));
    await settle();
    const added = backend.expectOne('/graphql');
    expect(added.request.headers.get('Authorization')).toBe(`Bearer ${user('bob').token}`);
    expect(added.request.body.query).toContain(FOURTH_SOURCE.field);
    await respond(added, timelineBody(expanded, { [FOURTH_SOURCE.id]: [wireEvent('cert')] }));
    expect(rows().some((row) => row.dataset['source'] === FOURTH_SOURCE.id)).toBe(true);

    await session.select('dave');
    await settle();
    const metadata = backend.expectOne('/timeline-sources');
    metadata.flush(catalog([]));
    await settle();
    const request = backend.expectOne('/graphql');
    expect(request.request.headers.get('Authorization')).toBe(`Bearer ${user('dave').token}`);
    expect(request.request.body.variables).toEqual({ id: 'dev-00001' });
    expect(request.request.body.query).not.toContain('$since');
    expect(request.request.body.query).not.toContain('patchTimeline');
    await respond(request, { data: { device: null } });
    expect(stateCard()).toContain('not found in TenantB');
  });

  it('shows invalid and unsupported metadata explicitly, and Retry reloads the catalog', async () => {
    const metadata = await begin();
    metadata.flush({ ...catalog(), version: 2 });
    await settle();
    backend.expectNone('/graphql');
    expect(stateCard()).toContain('Timeline metadata unavailable');
    expect(stateCard()).toContain('Unsupported timeline catalog version 2');

    el.querySelector<HTMLButtonElement>('app-state-card button')!.click();
    await settle();
    backend.expectOne('/timeline-sources').flush(catalog());
    await settle();
    const request = backend.expectOne('/graphql');
    await respond(request, timelineBody(TIMELINE_SOURCES, {}));
    expect(el.querySelector('app-state-card')).toBeNull();
  });

  it('keeps not-found and transport failures visible', async () => {
    await respond(await open(catalog([]), 'dave'), { data: { device: null } });
    expect(stateCard()).toContain('not found in TenantB');

    fixture.destroy();
    const req = await open(catalog([]));
    req.flush('', { status: 401, statusText: 'Unauthorized' });
    await settle();
    expect(stateCard()).toContain('Not authenticated (HTTP 401)');
  });
});
