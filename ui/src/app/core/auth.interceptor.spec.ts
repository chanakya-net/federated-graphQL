import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { authInterceptor } from './auth.interceptor';
import { SessionService } from './session.service';

describe('authInterceptor', () => {
  let token: string | null;
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    token = 'jwt-of-bob';
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: SessionService, useValue: { token: () => token } },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  function authorizationOf(url: string, method: 'GET' | 'POST' = 'POST'): string | null {
    (method === 'GET' ? http.get(url) : http.post(url, {})).subscribe();
    const req = backend.expectOne(url);
    req.flush({});
    return req.request.headers.get('Authorization');
  }

  it('adds the bearer token to /graphql', () => {
    expect(authorizationOf('/graphql')).toBe('Bearer jwt-of-bob');
    expect(authorizationOf('/graphql/', 'GET')).toBe('Bearer jwt-of-bob');
  });

  it('leaves every other URL alone', () => {
    expect(authorizationOf('/tokens.json', 'GET')).toBeNull();
    expect(authorizationOf('/graphqlish')).toBeNull();
    expect(authorizationOf('http://elsewhere.example/graphql')).toBeNull();
  });

  it('adds nothing when no user is selected', () => {
    token = null;
    expect(authorizationOf('/graphql')).toBeNull();
  });
});
