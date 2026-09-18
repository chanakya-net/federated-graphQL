import { DatePipe, DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { CombinedGraphQLErrors, ServerError } from '@apollo/client';
import { debounceTime, distinctUntilChanged, map } from 'rxjs';

import { SessionService } from '../../core/session.service';
import { QueryResult, watchQuery } from '../../core/watch-query';
import { DEVICE_SEARCH } from '../../graphql/operations';
import type { Device, DeviceSearchData } from '../../graphql/types';
import { extractErrors } from '../../timeline/section-state';
import { StateCardComponent } from '../../shared/state-card.component';

const PAGE_SIZE = 25;

export type SearchView =
  | { kind: 'loading' }
  | { kind: 'error'; title: string; message: string }
  | { kind: 'ok'; totalCount: number; items: Device[] };

export function searchView(result: QueryResult<DeviceSearchData>): SearchView {
  const devices = result.data?.devices;
  if (devices) return { kind: 'ok', totalCount: devices.totalCount, items: devices.items };
  if (result.loading) return { kind: 'loading' };
  const error = result.error;
  if (error != null && !CombinedGraphQLErrors.is(error)) {
    const status = ServerError.is(error) ? error.statusCode : null;
    return {
      kind: 'error',
      title: status === 401 ? 'Not authenticated (HTTP 401)' : 'Gateway unreachable',
      message: error instanceof Error ? error.message : String(error),
    };
  }
  const errors = extractErrors(result);
  return {
    kind: 'error',
    title: 'Device directory unavailable',
    message: errors[0]?.message ?? 'The gateway returned neither data nor errors.',
  };
}

/** Device search: `?q=` and `?page=` live in the URL so "back" from a timeline restores the list. */
@Component({
  selector: 'app-device-search-page',
  imports: [
    DatePipe,
    DecimalPipe,
    ReactiveFormsModule,
    RouterLink,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatTableModule,
    MatTooltipModule,
    StateCardComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './device-search.page.html',
  styleUrl: './device-search.page.scss',
})
export class DeviceSearchPage {
  private readonly router = inject(Router);
  protected readonly session = inject(SessionService);

  /** Query parameters, bound by the router (`withComponentInputBinding`). */
  readonly q = input<string | undefined>();
  readonly page = input<string | undefined>();

  protected readonly columns = ['id', 'hostname', 'os', 'ipAddress', 'lastSeenAt'];
  protected readonly pageSize = PAGE_SIZE;
  protected readonly searchControl = new FormControl('', { nonNullable: true });
  private readonly attempt = signal(0);

  protected readonly search = computed(() => this.q()?.trim() ?? '');
  protected readonly pageIndex = computed(() =>
    Math.max(0, Number.parseInt(this.page() ?? '0', 10) || 0),
  );

  private readonly request = computed(() => {
    const user = this.session.selected();
    if (!user) return null;
    return {
      variables: {
        search: this.search() || null,
        first: PAGE_SIZE,
        offset: this.pageIndex() * PAGE_SIZE,
      },
      user: user.sub,
      attempt: this.attempt(),
    };
  });

  private readonly result = watchQuery(DEVICE_SEARCH, this.request);
  protected readonly view = computed(() => searchView(this.result()));
  protected readonly skeletonRows = Array.from({ length: 8 }, (_, i) => i);
  /** The last `q` this page wrote to the URL, so the URL -> box sync does not undo newer typing. */
  private pushed: string | null = null;

  constructor() {
    // URL -> input box: initial load and back/forward, not the echo of our own navigation.
    effect(() => {
      const q = this.q() ?? '';
      if (q === this.pushed) return;
      this.pushed = null;
      if (q !== this.searchControl.value.trim())
        this.searchControl.setValue(q, { emitEvent: false });
    });
    // Input box -> URL, debounced. A new search starts on the first page.
    this.searchControl.valueChanges
      .pipe(
        debounceTime(300),
        map((value) => value.trim()),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((q) => {
        this.pushed = q;
        this.navigate({ q: q || null, page: null });
      });
  }

  protected onPage(event: PageEvent): void {
    this.navigate({ page: event.pageIndex > 0 ? event.pageIndex : null });
  }

  protected retry(): void {
    this.attempt.update((n) => n + 1);
  }

  protected open(device: Device): void {
    void this.router.navigate(['/devices', device.id]);
  }

  private navigate(queryParams: Record<string, string | number | null>): void {
    void this.router.navigate([], { queryParams, queryParamsHandling: 'merge', replaceUrl: true });
  }
}
