import { formatDate } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  LOCALE_ID,
  afterRenderEffect,
  computed,
  inject,
  input,
  model,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { eventKey, sourceMeta, type TimelineEvent } from '../../timeline/timeline.models';

interface Point {
  key: string;
  event: TimelineEvent;
  color: string;
  /** "Aug 25" */
  day: string;
  /** Full date, source and title: the accessible name and the tooltip. */
  description: string;
  first: boolean;
  last: boolean;
}

interface Month {
  key: string;
  label: string;
  points: Point[];
}

/**
 * The events as points on one horizontal track, oldest on the left, grouped by month. Each point is
 * in its subgraph's colour; clicking one selects it (`selected`, the event key, two-way), clicking it
 * again clears the selection. Arrow keys move between points; the track scrolls to keep the selected
 * point in view and starts at the newest event.
 */
@Component({
  selector: 'app-timeline-strip',
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <div class="strip skeleton" aria-busy="true" aria-label="Loading timeline">
        @for (i of skeletonPoints; track i) {
          <span class="skeleton-point">
            <span class="skeleton-bar w"></span>
            <span class="skeleton-bar d"></span>
            <span class="skeleton-bar w"></span>
          </span>
        }
      </div>
    } @else if (events().length === 0) {
      <p class="empty">
        {{ total() === 0 ? 'No events in the sections shown.' : 'No events match the filters.' }}
      </p>
    } @else {
      <div class="strip">
        <button
          mat-icon-button
          type="button"
          class="nav"
          aria-label="Scroll to earlier events"
          (click)="page(-1)"
        >
          <mat-icon>chevron_left</mat-icon>
        </button>
        <div class="scroller" #scroller (keydown)="onKeydown($event)">
          <ol class="months" aria-label="Timeline">
            @for (m of months(); track m.key) {
              <li class="month">
                <span class="month-label">{{ m.label }}</span>
                <ol class="points">
                  @for (p of m.points; track p.key) {
                    <li
                      class="point"
                      [class.first]="p.first"
                      [class.last]="p.last"
                      [attr.data-source]="p.event.source"
                      [style.--source-color]="p.color"
                    >
                      <button
                        type="button"
                        class="point-button"
                        [class.selected]="p.key === selected()"
                        [attr.aria-pressed]="p.key === selected()"
                        [attr.aria-label]="p.description"
                        [attr.data-key]="p.key"
                        [matTooltip]="p.description"
                        matTooltipPosition="above"
                        (click)="toggle(p.key)"
                      >
                        <span class="label">{{ p.event.label }}</span>
                        <span class="dot"></span>
                        <time class="when" [attr.datetime]="p.event.occurredAt">{{ p.day }}</time>
                      </button>
                    </li>
                  }
                </ol>
              </li>
            }
          </ol>
        </div>
        <button
          mat-icon-button
          type="button"
          class="nav"
          aria-label="Scroll to later events"
          (click)="page(1)"
        >
          <mat-icon>chevron_right</mat-icon>
        </button>
      </div>
    }
  `,
  styleUrl: './timeline-strip.component.scss',
})
export class TimelineStripComponent {
  /** Events after filtering, newest first (as the page holds them). */
  readonly events = input.required<readonly TimelineEvent[]>();
  /** Events before filtering, for the empty-state copy. */
  readonly total = input(0);
  readonly loading = input(false);
  /** The selected event's key (`eventKey`), or null. */
  readonly selected = model<string | null>(null);

  private readonly locale = inject(LOCALE_ID);
  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');

  protected readonly skeletonPoints = Array.from({ length: 8 }, (_, i) => i);

  /** Oldest first, grouped by the local month. */
  protected readonly months = computed<Month[]>(() => {
    const events = this.events();
    const months: Month[] = [];
    for (let i = events.length - 1; i >= 0; i--) {
      const e = events[i];
      const date = new Date(e.occurredAt);
      const key = `${date.getFullYear()}-${date.getMonth()}`;
      const meta = sourceMeta(e.source);
      const point: Point = {
        key: eventKey(e),
        event: e,
        color: meta.color,
        day: formatDate(date, 'MMM d', this.locale),
        description: `${formatDate(date, 'medium', this.locale)} · ${meta.name} · ${e.title}`,
        first: i === events.length - 1,
        last: i === 0,
      };
      const last = months.at(-1);
      if (last?.key === key) last.points.push(point);
      else months.push({ key, label: formatDate(date, 'MMM y', this.locale), points: [point] });
    }
    return months;
  });

  constructor() {
    // After every render: keep the selected point in view, or jump to the newest event when the
    // events change without a selection.
    afterRenderEffect(() => {
      const scroller = this.scroller()?.nativeElement;
      const key = this.selected();
      this.months();
      if (!scroller) return;
      const target = key ? this.buttons(scroller).find((b) => b.dataset['key'] === key) : undefined;
      if (target) this.reveal(scroller, target);
      else if (!key) scrollTo(scroller, scroller.scrollWidth, false);
    });
  }

  protected toggle(key: string): void {
    this.selected.set(this.selected() === key ? null : key);
  }

  protected page(direction: -1 | 1): void {
    const scroller = this.scroller()?.nativeElement;
    if (scroller) scrollTo(scroller, scroller.scrollLeft + direction * scroller.clientWidth * 0.8);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const scroller = this.scroller()?.nativeElement;
    const from = (event.target as HTMLElement | null)?.closest<HTMLButtonElement>('.point-button');
    if (!scroller || !from) return;
    const buttons = this.buttons(scroller);
    const i = buttons.indexOf(from);
    const to = { ArrowLeft: i - 1, ArrowRight: i + 1, Home: 0, End: buttons.length - 1 }[event.key];
    if (to === undefined || to < 0 || to >= buttons.length) return;
    event.preventDefault();
    buttons[to].focus();
  }

  private buttons(scroller: HTMLElement): HTMLButtonElement[] {
    return [...scroller.querySelectorAll<HTMLButtonElement>('.point-button')];
  }

  /** Scrolls only when the point is not fully visible, so clicking a visible point does not move it. */
  private reveal(scroller: HTMLElement, target: HTMLElement): void {
    const outer = scroller.getBoundingClientRect();
    const rect = target.getBoundingClientRect();
    if (rect.left >= outer.left && rect.right <= outer.right) return;
    const offset = rect.left - outer.left - (outer.width - rect.width) / 2;
    scrollTo(scroller, scroller.scrollLeft + offset);
  }
}

/**
 * Smooth for moves the user asked for, unless they prefer reduced motion; instant otherwise. Plain
 * assignment where `scrollTo` is missing (jsdom).
 */
function scrollTo(scroller: HTMLElement, left: number, smooth = true): void {
  const reduce = globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  if (typeof scroller.scrollTo === 'function')
    scroller.scrollTo({ left, behavior: smooth && !reduce ? 'smooth' : 'auto' });
  else scroller.scrollLeft = left;
}
