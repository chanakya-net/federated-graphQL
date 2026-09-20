import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import type { FieldView } from '../../finder/field-state';

interface SectionMeta {
  source: string;
  name: string;
  icon: string;
  color: string;
}

/**
 * How one category answered the sets query, in the category's colour, with the timeline's vocabulary:
 * ok (its filters count as their sets), no access (lock) or unavailable (warning). A failed category's
 * filters count as matching nothing, and this card says so.
 */
@Component({
  selector: 'app-source-status',
  imports: [MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': 'kind()',
    '[attr.data-category]': 'section().source',
    '[style.--source-color]': 'section().color',
  },
  template: `
    @let s = state();
    <div class="card" [attr.role]="kind() === 'ok' || kind() === 'loading' ? 'status' : 'alert'">
      <span class="swatch" aria-hidden="true">
        <mat-icon>{{
          kind() === 'no-access'
            ? 'lock'
            : kind() === 'ok' || kind() === 'loading'
              ? section().icon
              : 'warning'
        }}</mat-icon>
      </span>
      <div class="text">
        <span class="name">{{ section().name }}</span>
        @switch (s.kind) {
          @case ('ok') {
            <span class="copy"
              >{{ filterCount() }} filter{{ filterCount() === 1 ? '' : 's' }} applied</span
            >
          }
          @case ('loading') {
            <span class="skeleton-bar count-skeleton"></span>
          }
          @case ('no-access') {
            <span class="copy"
              >You don't have access to {{ section().name }} data: its filters match nothing.</span
            >
          }
          @case ('unavailable') {
            <span class="copy"
              >{{ section().name }} service is currently unavailable — its filters match
              nothing.</span
            >
            <span class="detail">Gateway error: {{ s.message }}</span>
          }
          @case ('transport-error') {
            <span class="copy"
              >{{ section().name }} could not be queried — its filters match nothing.</span
            >
            <span class="detail">{{ s.message }}</span>
          }
        }
      </div>
      @if (kind() === 'unavailable' || kind() === 'transport-error') {
        <button mat-button type="button" (click)="retry.emit()">Retry</button>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .card {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 10px 14px;
      border-radius: 16px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface-container-lowest);
      box-shadow: 0 1px 2px rgb(0 0 0 / 0.04);
    }
    .swatch {
      flex: none;
      display: grid;
      place-items: center;
      width: 36px;
      height: 36px;
      border-radius: 12px;
      background: color-mix(in srgb, var(--source-color) 14%, white);
      color: var(--source-color);
    }
    .text {
      display: flex;
      flex-direction: column;
      min-width: 0;
      flex: 1;
    }
    .name {
      font: var(--mat-sys-label-medium);
      letter-spacing: 0.02em;
      text-transform: uppercase;
      color: var(--mat-sys-on-surface-variant);
    }
    .copy {
      font: var(--mat-sys-body-medium);
    }
    .detail {
      font: var(--mat-sys-body-small);
      opacity: 0.8;
    }
    .count-skeleton {
      width: 64px;
      margin-top: 6px;
    }
    :host(.no-access) .card {
      background: var(--mat-sys-surface-container);
      color: var(--mat-sys-on-surface-variant);
      border: 1px dashed var(--mat-sys-outline);
      box-shadow: none;
    }
    :host(.no-access) .swatch {
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface-variant);
    }
    :host(.unavailable) .card,
    :host(.transport-error) .card {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
      border: 1px solid var(--mat-sys-error);
    }
    :host(.unavailable) .swatch,
    :host(.transport-error) .swatch {
      background: color-mix(in srgb, var(--mat-sys-error) 16%, white);
      color: var(--mat-sys-error);
    }
  `,
})
export class SourceStatusComponent {
  readonly section = input.required<SectionMeta>();
  readonly state = input.required<FieldView<unknown>>();
  readonly filterCount = input(0);
  readonly retry = output<void>();

  protected readonly kind = computed(() => this.state().kind);
}
