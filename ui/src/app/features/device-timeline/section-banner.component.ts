import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import type { SectionState } from '../../timeline/section-state';
import type { TimelineSource } from '../../timeline/timeline-catalog';

// Banner copy, exact (plan §7).
const noAccessCopy = (s: TimelineSource) => `You don't have access to ${s.name} data.`;
const unavailableCopy = (s: TimelineSource) =>
  `${s.name} service is currently unavailable — ${s.history} history is not shown.`;

/**
 * The state of one timeline section, as a card in the subgraph's colour (so the three cards double as
 * the legend of the timeline). `ok`: name and event count. `no-access`: lock, neutral colours.
 * `unavailable`: warning, the theme's error colours. `null`: still loading.
 */
@Component({
  selector: 'app-section-banner',
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': 'kind()',
    '[attr.data-section]': 'section().id',
    '[style.--source-color]': 'section().color',
  },
  template: `
    @let s = state();
    @switch (s?.kind) {
      @case ('ok') {
        <div class="line" role="status">
          <span class="swatch" aria-hidden="true"
            ><mat-icon class="icon">{{ section().icon }}</mat-icon></span
          >
          <span class="text">
            <span class="name">{{ section().name }}</span>
            <span class="count">{{ count() }} event{{ count() === 1 ? '' : 's' }}</span>
          </span>
        </div>
      }
      @case ('no-access') {
        <div class="banner" role="status">
          <span class="swatch" aria-hidden="true"><mat-icon class="icon">lock</mat-icon></span>
          <div class="text">
            <span class="name">{{ section().name }}</span>
            <p class="copy">{{ noAccess() }}</p>
          </div>
        </div>
      }
      @case ('unavailable') {
        <div class="banner" role="alert">
          <span class="swatch" aria-hidden="true"><mat-icon class="icon">warning</mat-icon></span>
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
          <span class="swatch" aria-hidden="true"
            ><mat-icon class="icon">{{ section().icon }}</mat-icon></span
          >
          <span class="text">
            <span class="name">{{ section().name }}</span>
            <span class="skeleton-bar count-skeleton"></span>
          </span>
        </div>
      }
    }
  `,
  styleUrl: './section-banner.component.scss',
})
export class SectionBannerComponent {
  readonly section = input.required<TimelineSource>();
  readonly state = input.required<SectionState<unknown> | null>();

  protected readonly kind = computed(() => this.state()?.kind ?? 'loading');
  protected readonly count = computed(() => {
    const s = this.state();
    return s?.kind === 'ok' ? s.events.length : 0;
  });
  protected readonly noAccess = computed(() => noAccessCopy(this.section()));
  protected readonly unavailable = computed(() => unavailableCopy(this.section()));
}
