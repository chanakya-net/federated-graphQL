import { ChangeDetectionStrategy, Component, input, model } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { MAX_FILTERS, filterKey, type Connector, type Filter } from '../../finder/finder.models';
import { providerMeta } from '../../finder/finder.models';
import type { SearchCapability } from '../../graphql/types';

/**
 * The expression as a sentence: one chip per filter in its category's colour, with a clickable AND / OR
 * connector before every chip but the first. Reads left to right; AND binds tighter than OR.
 */
@Component({
  selector: 'app-filter-expression',
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (filters().length === 0) {
      <p class="empty">
        <mat-icon aria-hidden="true">rule</mat-icon>
        No filters yet. Pick a catalog item above; join filters with AND / OR.
      </p>
    } @else {
      <ol class="expression" aria-label="Filter expression">
        @for (f of filters(); track keyOf(f); let i = $index) {
          @let meta = metaOf(f.category);
          <li class="term" [style.--source-color]="meta.color" [attr.data-category]="f.category">
            @if (i > 0) {
              <button
                type="button"
                class="connector"
                [class.or]="f.connector === 'or'"
                (click)="toggle(i)"
                [matTooltip]="'Click to change to ' + (f.connector === 'and' ? 'OR' : 'AND')"
                [attr.aria-label]="'Joined by ' + f.connector.toUpperCase() + ', click to change'"
              >
                {{ f.connector.toUpperCase() }}
              </button>
            }
            <span class="chip-row" [matTooltip]="detailOf()(f)">
              <mat-icon class="chip-icon" aria-hidden="true">{{ meta.icon }}</mat-icon>
              <span class="chip-label">{{ labelOf()(f) }}</span>
              <button
                type="button"
                class="remove"
                (click)="remove(i)"
                [attr.aria-label]="'Remove ' + labelOf()(f)"
              >
                <mat-icon>cancel</mat-icon>
              </button>
            </span>
          </li>
        }
      </ol>
      <p class="hint">
        {{ filters().length }} of {{ max }} filters. AND binds first: A AND B OR C means (A AND B)
        OR C.
      </p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .empty,
    .hint {
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    .empty {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 10px 12px;
      border: 1px dashed var(--mat-sys-outline-variant);
      border-radius: 12px;
      font: var(--mat-sys-body-medium);
    }
    .expression {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      list-style: none;
      margin: 0 0 6px;
      padding: 0;
    }
    .term {
      display: inline-flex;
      align-items: center;
      gap: 8px;
    }
    .connector {
      padding: 4px 10px;
      border-radius: 8px;
      border: 1px solid var(--mat-sys-outline);
      background: var(--mat-sys-surface-container-high);
      color: var(--mat-sys-on-surface);
      font: var(--mat-sys-label-medium);
      font-weight: 700;
      letter-spacing: 0.06em;
      cursor: pointer;
    }
    .connector.or {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
      border-color: transparent;
    }
    .connector:hover {
      filter: brightness(0.95);
    }
    .chip-row {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 3px 4px 3px 10px;
      border-radius: 999px;
      background: color-mix(in srgb, var(--source-color) 12%, white);
      color: var(--source-color);
      font: var(--mat-sys-body-medium);
      font-weight: 500;
    }
    .chip-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .remove {
      display: grid;
      place-items: center;
      width: 24px;
      height: 24px;
      padding: 0;
      border: 0;
      border-radius: 50%;
      background: none;
      color: inherit;
      cursor: pointer;
      opacity: 0.7;
    }
    .remove:hover {
      opacity: 1;
      background: color-mix(in srgb, var(--source-color) 15%, transparent);
    }
    .remove mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
  `,
})
export class FilterExpressionComponent {
  readonly filters = model<readonly Filter[]>([]);
  readonly labelOf = input.required<(f: Filter) => string>();
  readonly detailOf = input<(f: Filter) => string>(() => '');

  protected readonly max = MAX_FILTERS;
  readonly capabilities = input<readonly SearchCapability[]>([]);
  protected readonly metaOf = (category: string) => providerMeta(this.capabilities(), category);
  protected readonly keyOf = filterKey;

  protected toggle(index: number): void {
    this.filters.update((list) =>
      list.map((f, i) =>
        i === index ? { ...f, connector: (f.connector === 'and' ? 'or' : 'and') as Connector } : f,
      ),
    );
  }

  protected remove(index: number): void {
    this.filters.update((list) => list.filter((_, i) => i !== index));
  }
}
