import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { CombinedGraphQLErrors, ServerError } from '@apollo/client';

import searchDev000 from '../../../testing/fixtures/gateway/search-alice-dev-000.json';
import devUsers from '../../../testing/tokens.dev.json';
import { authInterceptor } from '../../core/auth.interceptor';
import type { DemoUser } from '../../core/demo-user';
import { provideGraphql } from '../../core/graphql.provider';
import { SessionService } from '../../core/session.service';
import type { DeviceSearchData } from '../../graphql/types';
import { DeviceSearchPage, searchView } from './device-search.page';

describe('searchView', () => {
  const data = searchDev000.data as DeviceSearchData;

  it('ok with the items and the total', () => {
    expect(searchView({ loading: false, data })).toEqual({
      kind: 'ok',
      totalCount: data.devices.totalCount,
      items: data.devices.items,
    });
  });

  it('loading', () => {
    expect(searchView({ loading: true })).toEqual({ kind: 'loading' });
  });

  it('HTTP 401 and other transport errors', () => {
    const response = new Response(null, { status: 401 });
    expect(
      searchView({ loading: false, error: new ServerError('401', { response, bodyText: '' }) }),
    ).toMatchObject({
      kind: 'error',
      title: 'Not authenticated (HTTP 401)',
    });
    expect(searchView({ loading: false, error: new Error('offline') })).toMatchObject({
      title: 'Gateway unreachable',
    });
  });

  it('GraphQL errors without data: the device directory is unavailable', () => {
    const error = new CombinedGraphQLErrors({
      data: null,
      errors: [{ message: 'Unexpected Execution Error', path: ['devices'] }],
    });
    expect(searchView({ loading: false, data: null, error })).toEqual({
      kind: 'error',
      title: 'Device directory unavailable',
      message: 'Unexpected Execution Error',
    });
  });
});

describe('DeviceSearchPage', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;

  async function settle(ms = 0): Promise<void> {
    for (let i = 0; i < 5; i++) {
      TestBed.tick();
      await new Promise((resolve) => setTimeout(resolve, i === 0 ? ms : 0));
    }
  }

  async function start(url: string): Promise<TestRequest> {
    const session = TestBed.inject(SessionService);
    const loading = session.load();
    backend.expectOne('/tokens.json').flush(devUsers as DemoUser[]);
    await loading;
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    await settle();
    return backend.expectOne('/graphql');
  }

  const rows = () => [...harness.routeNativeElement!.querySelectorAll('tr.row')];

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: '', component: DeviceSearchPage },
            { path: 'devices/:id', children: [] },
          ],
          withComponentInputBinding(),
        ),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideGraphql(),
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('searches from the URL and renders one row per device', async () => {
    const req = await start('/?q=dev-000');
    expect(req.request.body.operationName).toBe('DeviceSearch');
    expect(req.request.body.variables).toEqual({ search: 'dev-000', first: 25, offset: 0 });
    req.flush(searchDev000);
    await settle();
    expect(rows()).toHaveLength(searchDev000.data.devices.items.length);
    expect(rows()[0].textContent).toContain(searchDev000.data.devices.items[0].id);
    const input = harness.routeNativeElement!.querySelector('input') as HTMLInputElement;
    expect(input.value).toBe('dev-000');
  });

  it('an empty search lists the first page; ?page= pages by 25', async () => {
    const req = await start('/?page=2');
    expect(req.request.body.variables).toEqual({ search: null, first: 25, offset: 50 });
    req.flush(searchDev000);
    await settle();
  });

  it('typing searches after a 300 ms debounce, from the first page', async () => {
    (await start('/?page=3')).flush(searchDev000);
    await settle();
    const input = harness.routeNativeElement!.querySelector('input') as HTMLInputElement;
    input.value = 'ubuntu';
    input.dispatchEvent(new Event('input'));
    await settle(100);
    backend.expectNone('/graphql');
    await settle(300);
    expect(TestBed.inject(Router).url).toBe('/?q=ubuntu');
    const req = backend.expectOne('/graphql');
    expect(req.request.body.variables).toEqual({ search: 'ubuntu', first: 25, offset: 0 });
    req.flush(searchDev000);
    await settle();
  });

  it('clicking a row opens the device timeline', async () => {
    (await start('/')).flush(searchDev000);
    await settle();
    (rows()[1] as HTMLElement).click();
    await settle();
    expect(TestBed.inject(Router).url).toBe(`/devices/${searchDev000.data.devices.items[1].id}`);
  });
});
