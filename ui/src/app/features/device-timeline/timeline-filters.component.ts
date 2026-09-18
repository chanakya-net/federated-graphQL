import { ChangeDetectionStrategy, Component, computed, effect, model } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import {
  DEFAULT_FILTER,
  SECTIONS,
  SOURCES,
  STATUSES,
  type Source,
  type TimelineFilter,
} from '../../timeline/timeline.models';

/** Start of the local day, as an ISO instant. */
export function startOfDayIso(date: Date): string {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).toISOString();
}

/** Last millisecond of the local day, as an ISO instant (`until` is inclusive). */
export function endOfDayIso(date: Date): string {
  return new Date(
    date.getFullYear(),
    date.getMonth(),
    date.getDate() + 1,
    0,
    0,
    0,
    -1,
  ).toISOString();
}

/**
 * Source toggles, date range, statuses and free text. Emits the whole filter through `filter`
 * (two-way). The page sends the date range to the server; everything else filters client-side.
 */
@Component({
  selector: 'app-timeline-filters',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="filters" role="search" aria-label="Timeline filters">
      <mat-button-toggle-group
        multiple
        hideMultipleSelectionIndicator
        class="sources"
        aria-label="Event types"
        [value]="sourceList()"
        (change)="setSources($event.value)"
      >
        @for (s of sections; track s.source) {
          <mat-button-toggle [value]="s.source">
            <mat-icon class="toggle-icon">{{ s.icon }}</mat-icon
            >{{ s.name }}
          </mat-button-toggle>
        }
      </mat-button-toggle-group>

      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="dates">
        <mat-label>Date range</mat-label>
        <mat-date-range-input [formGroup]="range" [rangePicker]="picker">
          <input
            matStartDate
            formControlName="start"
            placeholder="From"
            (dateChange)="applyRange()"
          />
          <input matEndDate formControlName="end" placeholder="To" (dateChange)="applyRange()" />
        </mat-date-range-input>
        @if (hasRange()) {
          <button
            mat-icon-button
            matIconSuffix
            type="button"
            aria-label="Clear date range"
            (click)="clearRange()"
          >
            <mat-icon>close</mat-icon>
          </button>
        }
        <mat-datepicker-toggle matIconSuffix [for]="picker" />
        <mat-date-range-picker #picker />
      </mat-form-field>

      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="statuses">
        <mat-label>Status</mat-label>
        <mat-select
          multiple
          [value]="statusList()"
          (valueChange)="setStatuses($event)"
          placeholder="All"
        >
          @for (s of statuses; track s) {
            <mat-option [value]="s">{{ s }}</mat-option>
          }
        </mat-select>
      </mat-form-field>

      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="text">
        <mat-label>Text</mat-label>
        <mat-icon matPrefix>filter_list</mat-icon>
        <input
          matInput
          [value]="filter().text"
          (input)="setText($any($event.target).value)"
          autocomplete="off"
        />
      </mat-form-field>

      <button mat-button type="button" (click)="reset()" [disabled]="isDefault()">Reset</button>
    </div>
  `,
  styles: `
    .filters {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 12px;
    }
    .toggle-icon {
      margin-right: 6px;
      vertical-align: middle;
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .dates {
      width: 290px;
    }
    .statuses {
      width: 180px;
    }
    .text {
      flex: 1 1 200px;
      min-width: 180px;
    }
  `,
})
export class TimelineFiltersComponent {
  readonly filter = model.required<TimelineFilter>();

  protected readonly sections = SECTIONS;
  protected readonly statuses = STATUSES;
  protected readonly range = new FormGroup({
    start: new FormControl<Date | null>(null),
    end: new FormControl<Date | null>(null),
  });

  protected readonly sourceList = computed(() =>
    SOURCES.filter((s) => this.filter().sources.has(s)),
  );
  protected readonly statusList = computed(() => [...this.filter().statuses]);
  protected readonly hasRange = computed(() => !!this.filter().since || !!this.filter().until);
  protected readonly isDefault = computed(() => {
    const f = this.filter();
    return (
      f.sources.size === SOURCES.length && !f.since && !f.until && f.statuses.size === 0 && !f.text
    );
  });

  constructor() {
    // A reset clears the picker too. Keyed on the range alone, so other filter changes leave a
    // half-picked range alone.
    const rangeKey = computed(() => `${this.filter().since ?? ''}|${this.filter().until ?? ''}`);
    effect(() => {
      if (rangeKey() === '|') this.range.setValue({ start: null, end: null }, { emitEvent: false });
    });
  }

  /**
   * Runs on `dateChange`: a date picked in the calendar, or typed and committed (blur / Enter), not
   * every keystroke ("8" already parses as a date). Only a complete, ordered range or none at all is
   * applied, so picking a range runs one server query.
   */
  protected applyRange(): void {
    const { start, end } = this.range.getRawValue();
    if (start && end && start <= end)
      this.patch({ since: startOfDayIso(start), until: endOfDayIso(end) });
    else if (!start && !end) this.patch({ since: undefined, until: undefined });
  }

  protected clearRange(): void {
    this.range.setValue({ start: null, end: null });
    this.patch({ since: undefined, until: undefined });
  }

  protected setSources(value: Source[]): void {
    this.patch({ sources: new Set(value) });
  }

  protected setStatuses(value: string[]): void {
    this.patch({ statuses: new Set(value) });
  }

  protected setText(text: string): void {
    this.patch({ text });
  }

  protected reset(): void {
    this.filter.set(DEFAULT_FILTER);
  }

  private patch(change: Partial<TimelineFilter>): void {
    this.filter.update((f) => ({ ...f, ...change }));
  }
}
