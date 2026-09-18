import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Apollo } from 'apollo-angular';
import { firstValueFrom } from 'rxjs';

import type { DemoUser } from './demo-user';

export const SELECTED_USER_KEY = 'sor.user';

/**
 * The demo users from `/tokens.json` and which one is selected. Pages read `selected()` as part of
 * their query parameters, so selecting another user re-runs every query with the new token.
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly http = inject(HttpClient);
  private readonly apollo = inject(Apollo);

  private readonly selectedSub = signal<string | null>(null);
  readonly users = signal<readonly DemoUser[]>([]);
  readonly selected = computed(
    () => this.users().find((u) => u.sub === this.selectedSub()) ?? null,
  );
  /** Why `/tokens.json` could not be loaded; `null` once it has been. */
  readonly loadError = signal<string | null>(null);

  /** Called once by the app initializer; never throws, so the shell can render an error state. */
  async load(): Promise<void> {
    this.loadError.set(null);
    try {
      const users = await firstValueFrom(this.http.get<DemoUser[]>('/tokens.json'));
      if (!Array.isArray(users) || users.length === 0)
        throw new Error('/tokens.json contains no users.');
      this.users.set(users);
      const stored = readStoredSub();
      this.selectedSub.set(users.some((u) => u.sub === stored) ? stored : users[0].sub);
    } catch (error) {
      this.users.set([]);
      this.selectedSub.set(null);
      this.loadError.set(describe(error));
    }
  }

  async select(sub: string): Promise<void> {
    if (sub === this.selectedSub() || !this.users().some((u) => u.sub === sub)) return;
    storeSub(sub);
    try {
      // First cancel and forget everything fetched with the previous user's token, then switch.
      // The other order lets the (deferred) clear cancel the new user's queries as well.
      await this.apollo.client.clearStore();
    } finally {
      this.selectedSub.set(sub);
    }
  }

  token(): string | null {
    return this.selected()?.token ?? null;
  }
}

function readStoredSub(): string | null {
  try {
    return localStorage.getItem(SELECTED_USER_KEY);
  } catch {
    return null;
  }
}

function storeSub(sub: string): void {
  try {
    localStorage.setItem(SELECTED_USER_KEY, sub);
  } catch {
    // Storage blocked (private mode): the selection just does not survive a reload.
  }
}

function describe(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    return error.status === 0
      ? 'Could not reach /tokens.json.'
      : `/tokens.json answered HTTP ${error.status}${error.statusText ? ` ${error.statusText}` : ''}.`;
  }
  return error instanceof Error ? error.message : String(error);
}
