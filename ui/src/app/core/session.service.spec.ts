import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Apollo } from 'apollo-angular';

import devUsers from '../../testing/tokens.dev.json';
import type { DemoUser } from './demo-user';
import { SELECTED_USER_KEY, SessionService } from './session.service';

describe('SessionService', () => {
  let clearStore: ReturnType<typeof vi.fn>;
  let backend: HttpTestingController;
  let session: SessionService;
  const users = devUsers as DemoUser[];

  beforeEach(() => {
    localStorage.clear();
    clearStore = vi.fn().mockResolvedValue([]);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Apollo, useValue: { client: { clearStore } } },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
  });

  afterEach(() => {
    backend.verify();
    localStorage.clear();
  });

  async function load(body: object = users, status = 200): Promise<void> {
    const done = session.load();
    const req = backend.expectOne('/tokens.json');
    if (status === 200) req.flush(body);
    else req.flush('', { status, statusText: 'Not Found' });
    await done;
  }

  it('loads the users and selects the first one by default', async () => {
    await load();
    expect(session.users().map((u) => u.sub)).toEqual(['alice', 'bob', 'carol', 'dave', 'erin']);
    expect(session.selected()?.sub).toBe('alice');
    expect(session.token()).toBe(users[0].token);
    expect(session.loadError()).toBeNull();
  });

  it('restores the selection from localStorage', async () => {
    localStorage.setItem(SELECTED_USER_KEY, 'dave');
    await load();
    expect(session.selected()?.sub).toBe('dave');
    expect(session.token()).toBe(users[3].token);
  });

  it('falls back to the first user when the stored one no longer exists', async () => {
    localStorage.setItem(SELECTED_USER_KEY, 'mallory');
    await load();
    expect(session.selected()?.sub).toBe('alice');
  });

  it('select switches the token, persists the choice and clears the Apollo store', async () => {
    await load();
    const switching = session.select('bob');
    expect(clearStore).toHaveBeenCalledTimes(1);
    expect(session.selected()?.sub).toBe('alice'); // the store is cleared before the switch
    await switching;
    expect(session.selected()?.sub).toBe('bob');
    expect(session.token()).toBe(users[1].token);
    expect(localStorage.getItem(SELECTED_USER_KEY)).toBe('bob');
    expect(clearStore).toHaveBeenCalledTimes(1);
  });

  it('select ignores the current user and unknown users', async () => {
    await load();
    await session.select('alice');
    await session.select('mallory');
    expect(session.selected()?.sub).toBe('alice');
    expect(clearStore).not.toHaveBeenCalled();
  });

  it('reports a failed load instead of throwing', async () => {
    await load([], 404);
    expect(session.users()).toEqual([]);
    expect(session.selected()).toBeNull();
    expect(session.token()).toBeNull();
    expect(session.loadError()).toBe('/tokens.json answered HTTP 404 Not Found.');
  });

  it('treats an empty users file as a failed load', async () => {
    await load([]);
    expect(session.loadError()).toBe('/tokens.json contains no users.');
  });
});
