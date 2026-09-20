import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  output,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import {
  MatAutocompleteModule,
  MatAutocompleteSelectedEvent,
} from '@angular/material/autocomplete';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { debounceTime, distinctUntilChanged, map } from 'rxjs';

import type { FieldView } from '../../finder/field-state';
import type { CatalogPick } from '../../finder/finder.models';
import type { SearchCapability } from '../../graphql/types';

/**
 * One category's picker: a text box that searches that category's catalog (debounced, through `search`)
 * and offers the matches as an autocomplete; choosing one emits `picked` and the box clears. The
 * catalog's state comes from the page: no access disables the picker with a lock, unavailable keeps it
 * usable but says why.
 */
@Component({
  selector: 'app-catalog-picker',
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': 'stateKind()',
    '[attr.data-category]': 'section().category',
    '[style.--source-color]': 'section().color',
  },
  template: `
    <mat-form-field appearance="outline" subscriptSizing="dynamic" class="field">
      <mat-label>
        <mat-icon class="label-icon" aria-hidden="true">{{ section().icon }}</mat-icon
        >{{ section().name }}
      </mat-label>
      <input
        #box
        matInput
        [formControl]="text"
        [placeholder]="placeholder()"
        [matAutocomplete]="auto"
        autocomplete="off"
        spellcheck="false"
      />
      @if (stateKind() === 'loading') {
        <mat-progress-spinner matSuffix mode="indeterminate" diameter="18" class="spinner" />
      } @else if (stateKind() === 'no-access') {
        <mat-icon matSuffix class="suffix-icon" aria-hidden="true">lock</mat-icon>
      } @else if (stateKind() !== 'ok') {
        <mat-icon matSuffix class="suffix-icon warn" aria-hidden="true">warning</mat-icon>
      } @else {
        <mat-icon matSuffix class="suffix-icon" aria-hidden="true">add_circle</mat-icon>
      }
      <mat-autocomplete #auto [displayWith]="displayNothing" (optionSelected)="add($event)">
        @for (o of options(); track o.key) {
          <mat-option [value]="o">
            <span class="option">
              <span class="option-label">{{ o.label }}</span>
              @if (o.detail) {
                <span class="option-detail">{{ o.detail }}</span>
              }
            </span>
          </mat-option>
        } @empty {
          @if (stateKind() === 'ok' && text.value.trim()) {
            <mat-option disabled
              >No {{ section().name.toLowerCase() }} matches “{{ text.value }}”.</mat-option
            >
          }
        }
      </mat-autocomplete>
      <mat-hint>{{ hint() }}</mat-hint>
    </mat-form-field>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .field {
      width: 100%;
    }
    .label-icon {
      margin-right: 6px;
      vertical-align: -4px;
      font-size: 18px;
      width: 18px;
      height: 18px;
      color: var(--source-color);
    }
    .spinner {
      margin-right: 12px;
    }
    .suffix-icon {
      margin-right: 8px;
      color: var(--mat-sys-on-surface-variant);
    }
    .suffix-icon.warn {
      color: var(--mat-sys-error);
    }
    :host(.no-access) .field {
      opacity: 0.75;
    }
    :host(.unavailable) mat-hint,
    :host(.transport-error) mat-hint {
      color: var(--mat-sys-error);
    }
    .option {
      display: flex;
      flex-direction: column;
      line-height: 1.3;
      padding: 2px 0;
    }
    .option-label {
      font: var(--mat-sys-body-medium);
      font-weight: 500;
    }
    .option-detail {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class CatalogPickerComponent {
  readonly section = input.required<SearchCapability>();
  readonly placeholder = input('Type to search');
  /** The catalog matches for the current search text (the page owns the query). */
  readonly options = input.required<readonly CatalogPick[]>();
  readonly state = input.required<FieldView<unknown>>();
  /** Whether the expression can take another filter. */
  readonly full = input(false);
  readonly search = output<string>();
  readonly picked = output<CatalogPick>();

  protected readonly text = new FormControl('', { nonNullable: true });
  protected readonly displayNothing = () => '';
  private readonly box = viewChild.required<ElementRef<HTMLInputElement>>('box');

  protected readonly stateKind = computed(() => this.state().kind);
  protected readonly hint = computed(() => {
    const s = this.state();
    const name = this.section().name;
    switch (s.kind) {
      case 'no-access':
        return `You don't have access to ${name} data.`;
      case 'unavailable':
        return `${name} service is currently unavailable — ${s.message}`;
      case 'transport-error':
        return `${name} catalog could not be loaded — ${s.message}`;
      default:
        return this.full() ? 'The expression is full.' : 'Pick one to add it to the expression.';
    }
  });

  constructor() {
    this.text.valueChanges
      .pipe(
        debounceTime(250),
        map((v) => v.trim()),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((term) => this.search.emit(term));
    // No access: the subgraph said no, so there is nothing to type for.
    effect(() => {
      const disabled = this.stateKind() === 'no-access' || this.full();
      if (disabled && this.text.enabled) this.text.disable({ emitEvent: false });
      else if (!disabled && this.text.disabled) this.text.enable({ emitEvent: false });
    });
  }

  protected add(event: MatAutocompleteSelectedEvent): void {
    const pick = event.option.value as CatalogPick;
    event.option.deselect();
    this.picked.emit(pick);
    this.box().nativeElement.value = '';
    this.text.setValue('');
  }
}
