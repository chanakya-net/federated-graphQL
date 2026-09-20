import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import catalogCves from '../../../testing/fixtures/gateway/search-catalog-vulnerability-alice.json';
import deniedCatalog from '../../../testing/fixtures/gateway/search-catalog-patch-denied-carol.json';
import patchCatalog from '../../../testing/fixtures/gateway/search-catalog-patch-alice.json';
import catalogSoftware from '../../../testing/fixtures/gateway/search-catalog-softwareinstall-alice.json';
import capabilitiesFixture from '../../../testing/fixtures/gateway/search-capabilities-alice.json';
import devUsers from '../../../testing/tokens.dev.json';
import { authInterceptor } from '../../core/auth.interceptor';
import type { DemoUser } from '../../core/demo-user';
import { provideGraphql } from '../../core/graphql.provider';
import { SELECTED_USER_KEY, SessionService } from '../../core/session.service';
import type { CatalogPick, Category } from '../../finder/finder.models';
import { DeviceFinderPage } from './device-finder.page';

// The real Apollo client and interceptor with only the HTTP backend replaced: recorded gateway bodies
// go in, the DOM comes out. Catalogs and the single search are answered by operation name.

const CAPABILITIES = capabilitiesFixture.data.searchCapabilities;
const CATALOGS: Record<string, object> = {
  SearchCapabilities: capabilitiesFixture,
  'SearchCatalog:patch': patchCatalog,
  'SearchCatalog:vulnerability': catalogCves,
  'SearchCatalog:softwareinstall': catalogSoftware,
};
// Deliberately small page with a larger server total: the browser must neither evaluate nor slice it.
function serverPage(
  id = 'server-selected-device',
  tenantId = 'TenantA',
  totalCount = 77,
  hasNextPage = true,
) {
  return {
    data: {
      findDevices: {
        totalCount,
        hasNextPage,
        items: [
          {
            device: {
              id,
              hostname: `host-${id}`,
              os: 'Linux',
              ipAddress: '10.0.0.1',
              lastSeenAt: '2026-09-20T00:00:00Z',
              tenantId,
            },
            events: [
              {
                id: `${id}-patch`,
                source: 'patch',
                itemKey: 'patch-0128',
                occurredAt: '2026-09-19T00:00:00Z',
                label: 'KB5000128',
                title: 'Patch title',
                subtitle: 'Vendor',
                status: 'APPLIED',
                severity: 'HIGH',
              },
              {
                id: `${id}-cve`,
                source: 'vulnerability',
                itemKey: 'CVE-2026-10166',
                occurredAt: '2026-09-19T00:00:00Z',
                label: 'CVE-2026-10166',
                title: 'CVE title',
                subtitle: 'Finding',
                status: 'OPEN',
                severity: 'HIGH',
              },
            ],
          },
        ],
      },
    },
  };
}

describe('DeviceFinderPage', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;

  async function settle(ms = 0): Promise<void> {
    for (let i = 0; i < 6; i++) {
      TestBed.tick();
      await new Promise((resolve) => setTimeout(resolve, i === 0 ? ms : 0));
    }
  }

  async function start(url: string, as = 'alice'): Promise<void> {
    localStorage.setItem(SELECTED_USER_KEY, as);
    const session = TestBed.inject(SessionService);
    const loading = session.load();
    backend.expectOne('/tokens.json').flush(devUsers as DemoUser[]);
    await loading;
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    await settle();
    backend
      .expectOne((request) => request.body?.operationName === 'SearchCapabilities')
      .flush(CATALOGS['SearchCapabilities']);
    await settle();
  }

  /** Every pending /graphql request, by operation name. */
  const pending = (): Map<string, TestRequest> => {
    const requests = backend.match((r) => r.url === '/graphql');
    const mapped = new Map(
      requests.map((req) => [
        req.request.body.operationName === 'SearchCatalog'
          ? `SearchCatalog:${req.request.body.variables.category}`
          : (req.request.body.operationName as string),
        req,
      ]),
    );
    expect(mapped.size).toBe(requests.length);
    return mapped;
  };

  /** Answers every pending request from `bodies` (unanswered ones fail the test in afterEach). */
  async function respond(bodies: Record<string, object>): Promise<Map<string, TestRequest>> {
    const requests = new Map<string, TestRequest>();
    for (let wave = 0; wave < 3; wave++) {
      const batch = pending();
      for (const [name, req] of batch) {
        expect(requests.has(name), `Duplicate request: ${name}`).toBe(false);
        requests.set(name, req);
        const body = bodies[name];
        expect(body, `Unexpected operation: ${name}`).toBeDefined();
        req.flush(body);
      }
      await settle();
    }
    return requests;
  }

  const el = () => harness.routeNativeElement!;
  const page = () =>
    harness.routeDebugElement!.componentInstance as DeviceFinderPage & {
      pick(category: Category, pick: CatalogPick): void;
    };
  const rows = () => [...el().querySelectorAll('app-finder-table tr.row')];
  const picker = (category: string) =>
    el().querySelector(`app-catalog-picker[data-category="${category}"]`)!;
  const terms = () => [...el().querySelectorAll('app-filter-expression .term')];
  const text = (node: Element | null) => node?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  /** "OR KB5000128": the connector (if any) and the label, without the icons' ligature text. */
  const term = (node: Element) =>
    [node.querySelector('.connector'), node.querySelector('.chip-label')]
      .map(text)
      .filter(Boolean)
      .join(' ');

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'find', component: DeviceFinderPage },
            { path: 'devices/:id', children: [] },
          ],
          withComponentInputBinding(),
        ),
        provideLocationMocks(),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideGraphql(),
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
    localStorage.clear();
  });

  it('with nothing selected: the three catalogs load, a hint says what to do, nothing else runs', async () => {
    await start('/find');
    const requests = await respond(CATALOGS);
    expect([...requests.keys()].sort()).toEqual([
      'SearchCatalog:patch',
      'SearchCatalog:softwareinstall',
      'SearchCatalog:vulnerability',
    ]);
    expect(requests.get('SearchCatalog:patch')!.request.body.variables).toEqual({
      category: 'patch',
      search: null,
      first: 25,
    });
    expect(text(el().querySelector('app-state-card'))).toContain('Nothing to find yet');
    expect(el().querySelector('app-finder-table')).toBeNull();
    expect(el().querySelectorAll('app-catalog-picker')).toHaveLength(3);
    expect(el().querySelector<HTMLButtonElement>('button.find')!.disabled).toBe(true);
  });

  it('uses one FindDevices request and renders exactly the server page and total', async () => {
    await start('/find?f=patch:patch-0128&f=and:cve:CVE-2026-10166&page=2');
    const requests = pending();
    expect([...requests.keys()].sort()).toEqual([
      'FindDevices',
      'SearchCatalog:patch',
      'SearchCatalog:softwareinstall',
      'SearchCatalog:vulnerability',
    ]);
    expect(requests.get('FindDevices')!.request.body.variables).toEqual({
      filters: [
        { category: 'patch', key: 'patch-0128', connector: 'and' },
        { category: 'vulnerability', key: 'CVE-2026-10166', connector: 'and' },
      ],
      first: 25,
      offset: 50,
    });
    for (const [name, req] of requests)
      req.flush(name === 'FindDevices' ? serverPage() : CATALOGS[name]);
    await settle();
    expect(rows().map((r) => text(r.querySelector('.id')))).toEqual(['server-selected-device']);
    expect(text(el().querySelector('.summary'))).toContain('77 devices match');
    expect(text(rows()[0])).toContain('KB5000128');
    expect(text(rows()[0])).toContain('CVE-2026-10166');
    expect(pending().size).toBe(0);
  });

  it.each(['AUTH_NOT_AUTHORIZED', 'SOURCE_UNAVAILABLE'])(
    'a required-source failure (%s) is an error, never a partial zero-result success, and can retry',
    async (code) => {
      await start('/find?f=patch:patch-0128&f=or:sw:Git', 'carol');
      await respond({
        ...CATALOGS,
        'SearchCatalog:patch': deniedCatalog,
        FindDevices: {
          data: { findDevices: null },
          errors: [
            { message: 'Required source failed', path: ['findDevices'], extensions: { code } },
          ],
        },
      });
      expect(picker('patch').classList).toContain('no-access');
      expect(text(el().querySelector('app-state-card'))).toContain('Search could not be completed');
      expect(el().querySelector('app-finder-table')).toBeNull();
      expect(text(el())).not.toContain('0 devices match');
      el().querySelector<HTMLButtonElement>('app-state-card button')!.click();
      await settle();
      const requests = await respond({ ...CATALOGS, FindDevices: serverPage() });
      expect(requests.has('FindDevices')).toBe(true);
      expect(rows()).toHaveLength(1);
      expect(pending().size).toBe(0);
    },
  );

  it('a GraphQL error with partial data does not display an incomplete result', async () => {
    await start('/find?f=patch:patch-0128');
    await respond({
      ...CATALOGS,
      FindDevices: {
        ...serverPage(),
        errors: [
          { message: 'Device enrichment failed', path: ['findDevices', 'items', 0, 'device'] },
        ],
      },
    });
    expect(el().querySelector('app-finder-table')).toBeNull();
    expect(text(el().querySelector('app-state-card'))).toContain('Device enrichment failed');
  });

  it('an HTTP failure shows Retry instead of a zero match count', async () => {
    await start('/find?f=patch:patch-0128');
    const requests = pending();
    for (const [name, req] of requests) {
      if (name === 'FindDevices')
        req.flush('Unavailable', { status: 503, statusText: 'Unavailable' });
      else req.flush(CATALOGS[name]);
    }
    await settle();
    expect(el().querySelector('app-finder-table')).toBeNull();
    expect(text(el().querySelector('app-state-card'))).toContain('Search could not be completed');
  });

  it('picking builds the expression, a connector flips to OR, Find writes it to the URL and runs it', async () => {
    await start('/find');
    await respond(CATALOGS);
    page().pick('patch', { key: 'patch-0128', label: 'KB5000128' });
    page().pick('vulnerability', { key: 'CVE-2026-10166', label: 'CVE-2026-10166' });
    page().pick('patch', { key: 'patch-0128', label: 'KB5000128' }); // a duplicate is ignored
    await settle();
    expect(terms().map(term)).toEqual(['KB5000128', 'AND CVE-2026-10166']);

    terms()[1].querySelector<HTMLButtonElement>('.connector')!.click();
    await settle();
    expect(terms().map(term)).toEqual(['KB5000128', 'OR CVE-2026-10166']);

    el().querySelector<HTMLButtonElement>('button.find')!.click();
    await settle();
    expect(TestBed.inject(Router).url).toBe('/find?f=patch:patch-0128&f=or:cve:CVE-2026-10166');
    const requests = await respond({ FindDevices: serverPage() });
    expect([...requests.keys()]).toEqual(['FindDevices']);
    expect(rows().length).toBeGreaterThan(0);

    // Removing a chip only changes the draft until Find is pressed again.
    terms()[1].querySelector<HTMLButtonElement>('.remove')!.click();
    await settle();
    expect(terms().map(term)).toEqual(['KB5000128']);
    expect(pending().size).toBe(0);
  });

  it('typing in a picker searches its catalog after a debounce; a page change stays in the URL', async () => {
    await start('/find?f=patch:patch-0128&f=or:patch:patch-0282');
    await respond({ ...CATALOGS, FindDevices: serverPage() });

    const input = picker('patch').querySelector('input')!;
    input.value = 'apple';
    input.dispatchEvent(new Event('input'));
    await settle(100);
    expect(pending().size).toBe(0);
    await settle(300);
    const search = await respond({ 'SearchCatalog:patch': patchCatalog });
    expect(search.get('SearchCatalog:patch')!.request.body.variables).toEqual({
      category: 'patch',
      search: 'apple',
      first: 25,
    });

    const paginator = el().querySelector('app-finder-table mat-paginator')!;
    paginator.querySelector<HTMLButtonElement>('.mat-mdc-paginator-navigation-next')!.click();
    await settle();
    expect(TestBed.inject(Router).url).toContain('page=1');
    const next = await respond({ FindDevices: serverPage('next-page') });
    expect([...next.keys()]).toEqual(['FindDevices']);
    expect(next.get('FindDevices')!.request.body.variables.offset).toBe(25);
    expect(rows().map((r) => text(r.querySelector('.id')))).toEqual(['next-page']);
    expect(pending().size).toBe(0);
  });

  it('Find on an existing result sends only the newly applied expression; repeating Find refreshes once', async () => {
    await start('/find?f=patch:patch-0128&page=1');
    await respond({ ...CATALOGS, FindDevices: serverPage() });
    page().pick('vulnerability', { key: 'CVE-2026-10166', label: 'CVE-2026-10166' });
    await settle();
    el().querySelector<HTMLButtonElement>('button.find')!.click();
    await settle();
    const requests = await respond({ FindDevices: serverPage() });
    expect([...requests.keys()]).toEqual(['FindDevices']);
    expect(requests.get('FindDevices')!.request.body.variables.filters).toHaveLength(2);
    expect(requests.get('FindDevices')!.request.body.variables.offset).toBe(0);
    el().querySelector<HTMLButtonElement>('button.find')!.click();
    await settle();
    expect([...(await respond({ FindDevices: serverPage() })).keys()]).toEqual(['FindDevices']);
    expect(pending().size).toBe(0);
  });

  it('back and forward restore the applied expression and server request', async () => {
    TestBed.inject(Router).setUpLocationChangeListener();
    await start('/find?f=patch:patch-0128');
    await respond({ ...CATALOGS, FindDevices: serverPage() });
    await harness.navigateByUrl('/find?f=cve:CVE-2026-10166&page=1');
    await settle();
    await respond({ FindDevices: serverPage('cve-page') });
    TestBed.inject(Location).back();
    await settle();
    expect(TestBed.inject(Router).url).toBe('/find?f=patch:patch-0128');
    const back = await respond({ FindDevices: serverPage('patch-page') });
    expect(back.get('FindDevices')!.request.body.variables).toEqual({
      filters: [{ category: 'patch', key: 'patch-0128', connector: 'and' }],
      first: 25,
      offset: 0,
    });
    expect(terms().map(term)).toEqual(['KB5000128']);
    TestBed.inject(Location).forward();
    await settle();
    const forward = await respond({ FindDevices: serverPage('cve-page') });
    expect(forward.get('FindDevices')!.request.body.variables.offset).toBe(25);
    expect(rows().map((r) => text(r.querySelector('.id')))).toEqual(['cve-page']);
  });

  it('a tenant switch clears old rows and requests the same filters with the new token', async () => {
    await start('/find?f=patch:patch-0128');
    await respond({ ...CATALOGS, FindDevices: serverPage() });
    await TestBed.inject(SessionService).select('dave');
    await settle();
    expect(text(el())).not.toContain('server-selected-device');
    expect(terms().map(term)).toEqual(['patch-0128']);
    const requests = await respond({
      ...CATALOGS,
      FindDevices: serverPage('tenant-b-device', 'TenantB', 1, false),
    });
    expect([...requests.keys()].sort()).toEqual([
      'FindDevices',
      'SearchCapabilities',
      'SearchCatalog:patch',
      'SearchCatalog:softwareinstall',
      'SearchCatalog:vulnerability',
    ]);
    const dave = (devUsers as DemoUser[]).find((user) => user.sub === 'dave')!;
    expect(requests.get('FindDevices')!.request.headers.get('Authorization')).toBe(
      `Bearer ${dave.token}`,
    );
    expect(rows().map((r) => text(r.querySelector('.id')))).toEqual(['tenant-b-device']);
    expect(pending().size).toBe(0);
  });

  it('a successful empty page retains the server total and a way back to previous pages', async () => {
    await start('/find?f=patch:patch-0128&page=3');
    await respond({
      ...CATALOGS,
      FindDevices: { data: { findDevices: { items: [], totalCount: 12, hasNextPage: false } } },
    });
    expect(text(el().querySelector('.summary'))).toContain('12 devices match');
    expect(text(el())).toContain('No devices on this page');
    expect(el().querySelector('mat-paginator')).not.toBeNull();
  });

  it('clicking a row opens the device timeline', async () => {
    await start('/find?f=cve:CVE-2026-10166');
    await respond({ ...CATALOGS, FindDevices: serverPage() });
    const id = text(rows()[0].querySelector('.id'));
    (rows()[0] as HTMLElement).click();
    await settle();
    expect(TestBed.inject(Router).url).toBe(`/devices/${id}`);
  });
  it('renders arbitrary server metadata, common catalog keys, columns and summaries without dates', async () => {
    await start('/find');
    await respond(CATALOGS);
    const capability = {
      ...CAPABILITIES[0],
      category: 'test-provider',
      name: 'Test Assets',
      icon: 'inventory_2',
      color: '#123456',
      placeholder: 'Find an asset',
    };
    // A refreshed server registry advertises a category absent from all client domain definitions.
    (page() as unknown as { retry(): void }).retry();
    await settle();
    const requests = await respond({
      SearchCapabilities: { data: { searchCapabilities: [capability] } },
      'SearchCatalog:test-provider': {
        data: {
          searchCatalog: [
            { key: 'asset:42', label: 'Asset Forty Two', detail: 'Provider summary' },
          ],
        },
      },
    });
    expect(requests.get('SearchCatalog:test-provider')!.request.body.operationName).toBe(
      'SearchCatalog',
    );
    expect(requests.get('SearchCatalog:test-provider')!.request.body.variables.category).toBe(
      'test-provider',
    );
    expect(picker('test-provider').querySelector('input')!.placeholder).toBe('Find an asset');
    expect(text(picker('test-provider'))).toContain('Test Assets');
    page().pick('test-provider', { key: 'asset:42', label: 'Asset Forty Two' });
    await settle();
    expect(terms()[0].querySelector('mat-icon')!.textContent).toContain('inventory_2');
    el().querySelector<HTMLButtonElement>('button.find')!.click();
    await settle();
    const result = serverPage();
    result.data.findDevices.items[0].events = [
      {
        ...result.data.findDevices.items[0].events[0],
        source: 'test-provider',
        itemKey: 'asset:42',
        label: 'Asset Forty Two',
        occurredAt: null as unknown as string,
      },
    ];
    const find = await respond({ FindDevices: result });
    expect(find.size).toBe(1);
    expect(find.get('FindDevices')!.request.body.variables.filters[0]).toEqual({
      category: 'test-provider',
      key: 'asset:42',
      connector: 'and',
    });
    expect(TestBed.inject(Router).url).toContain('test-provider:asset:42');
    expect(text(el().querySelector('th.mat-column-provider-test-provider'))).toContain(
      'Test Assets',
    );
    expect(text(rows()[0])).toContain('Asset Forty Two');
    expect(rows()[0].querySelector('time')).toBeNull();
  });

  it('keeps unknown URL categories in the expression and displays the server error', async () => {
    await start('/find?f=unregistered:item-1');
    const requests = await respond({
      ...CATALOGS,
      FindDevices: {
        data: { findDevices: null },
        errors: [
          {
            message: 'Unknown search provider: unregistered',
            path: ['findDevices'],
            extensions: { code: 'BAD_USER_INPUT' },
          },
        ],
      },
    });
    expect(requests.get('FindDevices')!.request.body.variables.filters).toEqual([
      { category: 'unregistered', key: 'item-1', connector: 'and' },
    ]);
    expect(text(terms()[0])).toContain('item-1');
    expect(text(el().querySelector('app-state-card'))).toContain('Unknown search provider');
    expect(el().querySelector('app-finder-table')).toBeNull();
  });

  it('disables an unavailable capability without issuing its catalog request', async () => {
    await start('/find');
    await respond(CATALOGS);
    (page() as unknown as { retry(): void }).retry();
    await settle();
    const requests = await respond({
      ...CATALOGS,
      SearchCapabilities: {
        data: {
          searchCapabilities: CAPABILITIES.map((c) => ({
            ...c,
            available: c.category !== 'patch',
          })),
        },
      },
    });
    expect(requests.has('SearchCatalog:patch')).toBe(false);
    expect(picker('patch').querySelector('input')!.disabled).toBe(true);
    expect(text(picker('patch'))).toContain("don't have access");
  });

  it('shows capability failures and supports retrying metadata discovery', async () => {
    await start('/find');
    await respond(CATALOGS);
    (page() as unknown as { retry(): void }).retry();
    await settle();
    await respond({
      SearchCapabilities: {
        data: null,
        errors: [{ message: 'Metadata unavailable', path: ['searchCapabilities'] }],
      },
    });
    expect(text(el())).toContain('Search catalogs could not be loaded');
    expect(text(el())).toContain('Metadata unavailable');
    expect(el().querySelectorAll('app-catalog-picker')).toHaveLength(0);
    el().querySelector<HTMLButtonElement>('app-state-card button')!.click();
    await settle();
    await respond(CATALOGS);
    expect(el().querySelectorAll('app-catalog-picker')).toHaveLength(3);
  });
});
