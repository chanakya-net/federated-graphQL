import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { App } from './app';
import { SessionService } from './core/session.service';

describe('App', () => {
  const loadError = signal<string | null>(null);
  const load = vi.fn();

  beforeEach(() => {
    loadError.set(null);
    load.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: SessionService,
          useValue: { loadError, load, selected: signal(null), users: signal([]) },
        },
      ],
    });
  });

  it('renders the shell with the router outlet', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('mat-toolbar')?.textContent).toContain('SoR Device Timeline');
    expect(el.querySelector('router-outlet')).not.toBeNull();
    expect(el.querySelector('app-state-card')).toBeNull();
  });

  it('shows an error with retry instead of a blank page when /tokens.json fails', async () => {
    loadError.set('/tokens.json answered HTTP 404 Not Found.');
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('router-outlet')).toBeNull();
    expect(el.querySelector('app-state-card')?.textContent).toContain('Demo users unavailable');
    el.querySelector<HTMLButtonElement>('app-state-card button')!.click();
    expect(load).toHaveBeenCalled();
  });
});
