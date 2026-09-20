import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { SessionService } from '../../core/session.service';
import { watchQuery } from '../../core/watch-query';
import { fieldView, type FieldView } from '../../finder/field-state';
import type { CatalogPick } from '../../finder/finder.models';
import { SEARCH_CATALOG } from '../../graphql/operations';
import type { SearchCapability, SearchCatalogItem } from '../../graphql/types';
import { CatalogPickerComponent } from './catalog-picker.component';

/** One provider owns one catalog request; editing an expression never reloads catalogs. */
@Component({
  selector: 'app-provider-catalog',
  imports: [CatalogPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-catalog-picker
      [section]="capability()"
      [placeholder]="capability().placeholder"
      [options]="options()"
      [state]="state()"
      [full]="full()"
      (search)="term.set($event)"
      (picked)="picked.emit($event)"
    />
  `,
})
export class ProviderCatalogComponent {
  readonly capability = input.required<SearchCapability>();
  readonly full = input(false);
  readonly attempt = input(0);
  readonly picked = output<CatalogPick>();
  readonly learned = output<readonly SearchCatalogItem[]>();
  private readonly session = inject(SessionService);
  protected readonly term = signal('');
  private readonly catalog = watchQuery(
    SEARCH_CATALOG,
    computed(() => {
      const user = this.session.selected();
      const capability = this.capability();
      if (!user || !capability.available) return null;
      return {
        variables: { category: capability.category, search: this.term() || null, first: 25 },
        user: user.sub,
        attempt: this.attempt(),
      };
    }),
  );
  protected readonly state = computed<FieldView<SearchCatalogItem[]>>(() => {
    if (!this.capability().available) return { kind: 'no-access' };
    const result = this.catalog();
    return fieldView(result.error ? { ...result, data: null } : result, 'searchCatalog');
  });
  protected readonly options = computed(() => {
    const state = this.state();
    return state.kind === 'ok' ? state.value : [];
  });
  constructor() {
    effect(() => this.learned.emit(this.options()));
  }
}
