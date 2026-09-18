import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import type { SectionState } from '../../timeline/section-state';
import type { SectionMeta } from '../../timeline/timeline.models';

// Banner copy, exact (plan §7).
const noAccessCopy = (s: SectionMeta) => `You don't have access to ${s.name} data.`;
const unavailableCopy = (s: SectionMeta) =>
  `${s.name} service is currently unavailable — ${s.history} history is not shown.`;

/**
 * The state of one timeline section. `ok`: a compact line with the event count. `no-access`: lock,
 * neutral colours. `unavailable`: warning, the theme's error colours. `null`: still loading.
 */
@Component({
  selector: 'app-section-banner',
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': 'kind()', '[attr.data-section]': 'section().key' },
  template: `
    @let s = state();
    @switch (s?.kind) {
      @case ('ok') {
        <div class="line" role="status">
          <mat-icon class="icon" aria-hidden="true">{{ section().icon }}</mat-icon>
          <span class="name">{{ section().name }}</span>
          <span class="count">{{ count() }} event{{ count() === 1 ? '' : 's' }}</span>
        </div>
      }
      @case ('no-access') {
        <div class="banner" role="status">
          <mat-icon class="icon" aria-hidden="true">lock</mat-icon>
          <div class="text">
            <span class="name">{{ section().name }}</span>
            <p class="copy">{{ noAccess() }}</p>
          </div>
        </div>
      }
      @case ('unavailable') {
        <div class="banner" role="alert">
          <mat-icon class="icon" aria-hidden="true">warning</mat-icon>
          <div class="text">
            <span class="name">{{ section().name }}</span>
            <p class="copy">{{ unavailable() }}</p>
            @if (s?.kind === 'unavailable') {
              <p class="detail">Gateway error: {{ s.message }}</p>
            }
          </div>
        </div>
      }
      @default {
        <div class="line" aria-busy="true">
          <mat-icon class="icon" aria-hidden="true">{{ section().icon }}</mat-icon>
          <span class="name">{{ section().name }}</span>
          <span class="skeleton-bar count-skeleton"></span>
        </div>
      }
    }
  `,
  styleUrl: './section-banner.component.scss',
})
export class SectionBannerComponent {
  readonly section = input.required<SectionMeta>();
  readonly state = input.required<SectionState<unknown> | null>();

  protected readonly kind = computed(() => this.state()?.kind ?? 'loading');
  protected readonly count = computed(() => {
    const s = this.state();
    return s?.kind === 'ok' ? s.events.length : 0;
  });
  protected readonly noAccess = computed(() => noAccessCopy(this.section()));
  protected readonly unavailable = computed(() => unavailableCopy(this.section()));
}
