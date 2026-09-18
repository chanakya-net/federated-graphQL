import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';

import { SessionService } from '../../core/session.service';
import { watchQuery } from '../../core/watch-query';
import { DEVICE_TIMELINE } from '../../graphql/operations';
import { StateCardComponent } from '../../shared/state-card.component';
import { timelineView, type TimelineSections } from '../../timeline/section-state';
import { applyFilter, merge } from '../../timeline/timeline-merge';
import { DEFAULT_FILTER, SECTIONS, type TimelineFilter } from '../../timeline/timeline.models';
import { SectionBannerComponent } from './section-banner.component';
import { TimelineFiltersComponent } from './timeline-filters.component';
import { TimelineListComponent } from './timeline-list.component';

/**
 * One device, one query, three sections. Page-level states (loading, transport error, not found,
 * directory down) are decided first; then each section is ok / no access / unavailable on its own.
 */
@Component({
  selector: 'app-device-timeline-page',
  imports: [
    DatePipe,
    RouterLink,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressBarModule,
    MatTooltipModule,
    StateCardComponent,
    SectionBannerComponent,
    TimelineFiltersComponent,
    TimelineListComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './device-timeline.page.html',
  styleUrl: './device-timeline.page.scss',
})
export class DeviceTimelinePage {
  protected readonly session = inject(SessionService);

  /** Route parameter `:id`, bound by the router (`withComponentInputBinding`). */
  readonly id = input.required<string>();

  protected readonly sectionList = SECTIONS;
  protected readonly filter = signal<TimelineFilter>(DEFAULT_FILTER);
  private readonly attempt = signal(0);

  /** Only the date range goes to the server; the other filters must not re-run the query. */
  private readonly range = computed(
    () => ({ since: this.filter().since ?? null, until: this.filter().until ?? null }),
    { equal: (a, b) => a.since === b.since && a.until === b.until },
  );

  private readonly request = computed(() => {
    const user = this.session.selected();
    if (!user) return null;
    return {
      variables: { id: this.id(), ...this.range() },
      user: user.sub,
      attempt: this.attempt(),
    };
  });

  private readonly result = watchQuery(DEVICE_TIMELINE, this.request);
  protected readonly view = computed(() => timelineView(this.result()));
  protected readonly sections = computed<TimelineSections | null>(() => {
    const v = this.view();
    return v.kind === 'ok' ? v.sections : null;
  });
  protected readonly events = computed(() => {
    const sections = this.sections();
    return sections ? merge(sections) : [];
  });
  protected readonly filtered = computed(() => applyFilter(this.events(), this.filter()));

  protected retry(): void {
    this.attempt.update((n) => n + 1);
  }

  protected async reloadUsers(): Promise<void> {
    await this.session.load();
    this.retry();
  }
}
