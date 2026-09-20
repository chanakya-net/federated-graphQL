import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';

import { SessionService } from '../../core/session.service';
import { watchDynamicQuery } from '../../core/watch-query';
import { StateCardComponent } from '../../shared/state-card.component';
import { timelineView, type TimelineSections } from '../../timeline/section-state';
import {
  buildTimelineQuery,
  validateTimelineCatalog,
  type DeviceTimelineData,
  type DeviceTimelineVars,
  type TimelineCatalog,
} from '../../timeline/timeline-catalog';
import { applyFilter, merge } from '../../timeline/timeline-merge';
import {
  defaultTimelineFilter,
  eventKey,
  type TimelineFilter,
} from '../../timeline/timeline.models';
import { EventDetailComponent } from './event-detail.component';
import { SectionBannerComponent } from './section-banner.component';
import { TimelineFiltersComponent } from './timeline-filters.component';
import { TimelineListComponent } from './timeline-list.component';
import { TimelineStripComponent } from './timeline-strip.component';
import { catchError, map, of, startWith, switchMap } from 'rxjs';

type CatalogState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ok'; catalog: TimelineCatalog };

/**
 * One device, one generated query, any number of sections. Page-level states (loading, transport error, not found,
 * directory down) are decided first; then each section is ok / no access / unavailable on its own.
 * The events are shown twice: as points on the horizontal strip and as a list; both select the same
 * event, whose details appear under the strip.
 */
@Component({
  selector: 'app-device-timeline-page',
  imports: [
    DatePipe,
    RouterLink,
    MatButtonModule,
    MatIconModule,
    MatProgressBarModule,
    MatTooltipModule,
    StateCardComponent,
    SectionBannerComponent,
    TimelineFiltersComponent,
    TimelineStripComponent,
    EventDetailComponent,
    TimelineListComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './device-timeline.page.html',
  styleUrl: './device-timeline.page.scss',
})
export class DeviceTimelinePage {
  protected readonly session = inject(SessionService);
  private readonly http = inject(HttpClient);

  /** Route parameter `:id`, bound by the router (`withComponentInputBinding`). */
  readonly id = input.required<string>();

  protected readonly filter = signal<TimelineFilter>(defaultTimelineFilter());
  /** The selected event's key; the detail panel is empty when it is filtered out. */
  protected readonly selectedKey = signal<string | null>(null);
  private readonly attempt = signal(0);

  /** Metadata is refreshed on navigation, Retry and selected-user changes. */
  private readonly catalogRequest = computed(() => ({
    id: this.id(),
    user: this.session.selected()?.sub ?? null,
    attempt: this.attempt(),
  }));
  protected readonly catalogState = toSignal(
    toObservable(this.catalogRequest).pipe(
      switchMap(() =>
        this.http.get<unknown>('/timeline-sources').pipe(
          map((value): CatalogState => ({ kind: 'ok', catalog: validateTimelineCatalog(value) })),
          catchError((error) =>
            of<CatalogState>({ kind: 'error', message: describeCatalogError(error) }),
          ),
          startWith({ kind: 'loading' } satisfies CatalogState),
        ),
      ),
    ),
    { initialValue: { kind: 'loading' } as CatalogState },
  );
  protected readonly sectionList = computed(() => {
    const state = this.catalogState();
    return state.kind === 'ok' ? state.catalog.sources : [];
  });
  private readonly builtQuery = computed(() => {
    const state = this.catalogState();
    return state.kind === 'ok' ? buildTimelineQuery(state.catalog) : null;
  });

  /** Only the date range goes to the server; the other filters must not re-run the query. */
  private readonly range = computed(
    () => ({ since: this.filter().since ?? null, until: this.filter().until ?? null }),
    { equal: (a, b) => a.since === b.since && a.until === b.until },
  );

  private readonly request = computed(() => {
    const user = this.session.selected();
    const built = this.builtQuery();
    if (!user || !built) return null;
    const variables: DeviceTimelineVars = built.usesRange
      ? { id: this.id(), ...this.range() }
      : { id: this.id() };
    return {
      query: built.document,
      variables,
      user: user.sub,
      attempt: this.attempt(),
    };
  });

  private readonly result = watchDynamicQuery<DeviceTimelineData, DeviceTimelineVars>(this.request);
  protected readonly view = computed(() => timelineView(this.result(), this.builtQuery()));
  protected readonly sections = computed<TimelineSections | null>(() => {
    const v = this.view();
    return v.kind === 'ok' ? v.sections : null;
  });
  protected readonly events = computed(() => {
    const sections = this.sections();
    return sections ? merge(sections) : [];
  });
  protected readonly filtered = computed(() => applyFilter(this.events(), this.filter()));
  protected readonly statuses = computed(() => {
    const values = new Set(this.sectionList().flatMap((source) => [...source.statuses]));
    for (const event of this.events()) values.add(event.status);
    return [...values];
  });
  protected readonly selected = computed(() => {
    const key = this.selectedKey();
    return key ? (this.filtered().find((e) => eventKey(e) === key) ?? null) : null;
  });

  constructor() {
    let previous = new Set<string>();
    effect(() => {
      const next = new Set(this.sectionList().map((source) => source.id));
      if (this.catalogState().kind !== 'ok') return;
      this.filter.update((filter) => {
        const previouslyAll =
          previous.size === 0 ||
          (filter.sources.size === previous.size &&
            [...previous].every((id) => filter.sources.has(id)));
        const sources = previouslyAll
          ? next
          : new Set([...filter.sources].filter((id) => next.has(id)));
        const allowedStatuses = new Set(
          this.sectionList().flatMap((source) => [...source.statuses]),
        );
        const statuses = new Set(
          [...filter.statuses].filter((status) => allowedStatuses.has(status)),
        );
        return { ...filter, sources, statuses };
      });
      previous = next;
    });
  }

  protected retry(): void {
    this.attempt.update((n) => n + 1);
  }

  protected async reloadUsers(): Promise<void> {
    await this.session.load();
    this.retry();
  }
}

function describeCatalogError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    return error.status === 0
      ? 'Could not reach /timeline-sources.'
      : `/timeline-sources answered HTTP ${error.status}${error.statusText ? ` ${error.statusText}` : ''}.`;
  }
  return error instanceof Error ? error.message : String(error);
}
