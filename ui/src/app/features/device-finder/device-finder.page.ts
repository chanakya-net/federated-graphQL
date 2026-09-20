import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Router } from '@angular/router';

import { SessionService } from '../../core/session.service';
import { QueryRequest, watchQuery } from '../../core/watch-query';
import { describe } from '../../finder/expression';
import { fieldView } from '../../finder/field-state';
import {
  MAX_FILTERS,
  PAGE_SIZE,
  categoriesOf,
  filterKey,
  filterParams,
  filtersEqual,
  pageParam,
  parseFilters,
  type Category,
  type Filter,
  type ParamValue,
  type CatalogPick,
} from '../../finder/finder.models';
import { FIND_DEVICES, SEARCH_CAPABILITIES } from '../../graphql/operations';
import { StateCardComponent } from '../../shared/state-card.component';
import { ProviderCatalogComponent } from './provider-catalog.component';
import { FilterExpressionComponent } from './filter-expression.component';
import { FinderRow, FinderTableComponent } from './finder-table.component';

/** The URL owns the applied expression and page; Device Search returns the complete result page. */
@Component({
  selector: 'app-device-finder-page',
  imports: [
    MatButtonModule,
    MatIconModule,
    StateCardComponent,
    ProviderCatalogComponent,
    FilterExpressionComponent,
    FinderTableComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './device-finder.page.html',
  styleUrl: './device-finder.page.scss',
})
export class DeviceFinderPage {
  private readonly router = inject(Router);
  protected readonly session = inject(SessionService);

  /** Query parameters, bound by the router (`withComponentInputBinding`); `f` repeats, one per filter. */
  readonly f = input<ParamValue>();
  readonly page = input<ParamValue>();

  /** The expression the URL says to evaluate. Equal expressions stay the same value; pagination is sent to the server. */
  protected readonly applied = computed(() => parseFilters(this.f()), { equal: filtersEqual });
  protected readonly appliedEmpty = computed(() => this.applied().length === 0);
  protected readonly categories = computed(() => categoriesOf(this.applied()));
  protected readonly pageIndex = computed(() => pageParam(this.page()));

  /** The expression being edited: chips and connectors, applied on Find. */
  protected readonly staged = signal<readonly Filter[]>([]);
  protected readonly stagedFull = computed(() => this.staged().length >= MAX_FILTERS);

  /** Labels seen so far (catalog options and result events), so a URL-restored filter reads well. */
  private readonly labels = signal(new Map<string, CatalogPick>());
  /** Bumped by Find and Retry: re-runs the search. */
  private readonly attempt = signal(0);
  /** Bumped by Retry only: the catalogs do not change when the expression does. */
  private readonly catalogAttempt = signal(0);

  private readonly capabilityQuery = watchQuery(
    SEARCH_CAPABILITIES,
    computed(() => {
      const user = this.session.selected();
      return user ? { variables: {}, user: user.sub, attempt: this.catalogAttempt() } : null;
    }),
  );
  protected readonly capabilityView = computed(() => {
    const result = this.capabilityQuery();
    return fieldView(result.error ? { ...result, data: null } : result, 'searchCapabilities');
  });
  protected readonly capabilities = computed(() => {
    const view = this.capabilityView();
    return view.kind === 'ok' ? view.value : [];
  });
  protected readonly catalogAttemptValue = this.catalogAttempt.asReadonly();

  private readonly search = watchQuery(
    FIND_DEVICES,
    computed(() =>
      this.appliedEmpty()
        ? null
        : this.request({
            filters: [...this.applied()],
            first: PAGE_SIZE,
            offset: this.pageIndex() * PAGE_SIZE,
          }),
    ),
  );
  protected readonly searchView = computed(() => {
    const result = this.search();
    // Search is atomic: even partial GraphQL data must not look like a complete result.
    return fieldView(result.error ? { ...result, data: null } : result, 'findDevices');
  });
  protected readonly total = computed(() => {
    const view = this.searchView();
    return view.kind === 'ok' ? view.value.totalCount : 0;
  });
  protected readonly rows = computed<FinderRow[]>(() => {
    const view = this.searchView();
    if (view.kind !== 'ok') return [];
    return view.value.items.map((item) => {
      const cells: FinderRow['cells'] = Object.create(null);
      for (const event of item.events) (cells[event.source] ??= []).push(event);
      return { device: item.device, cells };
    });
  });

  private readonly labelKey = (filter: Pick<Filter, 'category' | 'key'>): string =>
    JSON.stringify([this.session.selected()?.sub, filter.category, filter.key]);

  protected readonly labelOf = (f: Filter): string =>
    this.labels().get(this.labelKey(f))?.label ?? f.key;
  protected readonly detailOf = (f: Filter): string =>
    this.labels().get(this.labelKey(f))?.detail ?? '';
  protected readonly expressionText = computed(() => describe(this.applied(), this.labelOf));

  constructor() {
    // Everything with a label teaches the cache: catalog matches and the events of the results.
    effect(() => {
      const result = this.searchView();
      if (result.kind !== 'ok') return;
      const learned = result.value.items.flatMap((item) =>
        item.events.map((event): [string, CatalogPick] => [
          this.labelKey({ category: event.source, key: event.itemKey }),
          { key: event.itemKey, label: event.label, detail: event.title },
        ]),
      );
      if (learned.length === 0) return;
      this.labels.update((map) => {
        let changed = false;
        for (const [key, pick] of learned) {
          if (!map.has(key)) {
            map.set(key, pick);
            changed = true;
          }
        }
        return changed ? new Map(map) : map;
      });
    });
    // URL -> chips: initial load, back/forward and our own Find.
    effect(() => this.staged.set(this.applied()));
  }

  protected learn(category: string, options: readonly CatalogPick[]): void {
    if (!options.length) return;
    this.labels.update((map) => {
      const next = new Map(map);
      for (const option of options) next.set(this.labelKey({ category, key: option.key }), option);
      return next;
    });
  }

  /** A picked catalog item joins the expression with AND (the connector chip flips it to OR). */
  protected pick(category: Category, pick: CatalogPick): void {
    const filter: Filter = { category, key: pick.key, connector: 'and' };
    this.labels.update((map) => {
      if (map.has(this.labelKey(filter))) return map;
      const next = new Map(map);
      next.set(this.labelKey(filter), pick);
      return next;
    });
    this.staged.update((list) =>
      list.length >= MAX_FILTERS || list.some((f) => filterKey(f) === filterKey(filter))
        ? list
        : [...list, filter],
    );
  }

  /** Applies the chips: a new URL (so "back" returns here) and a fresh run even for the same expression. */
  protected find(): void {
    if (this.staged().length === 0) return;
    if (filtersEqual(this.staged(), this.applied()) && this.pageIndex() === 0) {
      this.attempt.update((n) => n + 1);
    }
    void this.router.navigate([], {
      queryParams: { f: filterParams(this.staged()), page: null },
      queryParamsHandling: 'merge',
    });
  }

  protected clear(): void {
    this.staged.set([]);
    if (!this.appliedEmpty()) {
      void this.router.navigate([], {
        queryParams: { f: null, page: null },
        queryParamsHandling: 'merge',
      });
    }
  }

  protected onPage(pageIndex: number): void {
    const result = this.searchView();
    if (result.kind !== 'ok' || (pageIndex > this.pageIndex() && !result.value.hasNextPage)) return;
    void this.router.navigate([], {
      queryParams: { page: pageIndex > 0 ? pageIndex : null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected retry(): void {
    this.attempt.update((n) => n + 1);
    this.catalogAttempt.update((n) => n + 1);
  }

  private request<TVars extends object>(variables: TVars): QueryRequest<TVars> | null {
    const user = this.session.selected();
    return user ? { variables, user: user.sub, attempt: this.attempt() } : null;
  }
}
