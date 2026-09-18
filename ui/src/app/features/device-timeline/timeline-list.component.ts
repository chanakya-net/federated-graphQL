import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { sourceMeta, type TimelineEvent } from '../../timeline/timeline.models';

/** The merged timeline, newest first: source icon, local time (ISO in a tooltip), text, chips. */
@Component({
  selector: 'app-timeline-list',
  imports: [DatePipe, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <div class="skeleton" aria-busy="true" aria-label="Loading timeline">
        @for (row of skeletonRows; track row) {
          <div class="skeleton-row">
            <span class="skeleton-bar dot"></span><span class="skeleton-bar"></span
            ><span class="skeleton-bar"></span>
          </div>
        }
      </div>
    } @else if (events().length === 0) {
      <p class="empty">
        {{ total() === 0 ? 'No events in the sections shown.' : 'No events match the filters.' }}
      </p>
    } @else {
      <p class="summary">
        {{ events().length }} of {{ total() }} event{{ total() === 1 ? '' : 's' }}, newest first
      </p>
      <ol class="events" aria-label="Timeline">
        @for (e of events(); track e.source + ':' + e.id) {
          @let meta = sourceOf(e);
          <li class="event" [attr.data-source]="e.source">
            <mat-icon class="source" [attr.aria-label]="meta.name" [title]="meta.name">{{
              meta.icon
            }}</mat-icon>
            <time class="when" [attr.datetime]="e.occurredAt" [matTooltip]="e.occurredAt">
              {{ e.occurredAt | date: 'medium' }}
            </time>
            <div class="text">
              <div class="title">{{ e.title }}</div>
              <div class="subtitle">{{ e.subtitle }}</div>
            </div>
            <div class="chips">
              <span class="chip status" [attr.data-status]="e.status">{{ e.status }}</span>
              @if (e.severity) {
                <span class="chip severity" [attr.data-severity]="e.severity">{{
                  e.severity
                }}</span>
              }
            </div>
          </li>
        }
      </ol>
    }
  `,
  styleUrl: './timeline-list.component.scss',
})
export class TimelineListComponent {
  /** Events after filtering. */
  readonly events = input.required<readonly TimelineEvent[]>();
  /** Events before filtering. */
  readonly total = input.required<number>();
  readonly loading = input(false);

  protected readonly skeletonRows = Array.from({ length: 6 }, (_, i) => i);
  protected readonly sourceOf = (e: TimelineEvent) => sourceMeta(e.source);
}
