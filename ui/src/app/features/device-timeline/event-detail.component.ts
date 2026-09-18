import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import type { InstallEvent, PatchEvent, VulnerabilityEvent } from '../../graphql/types';
import { sourceMeta, type TimelineEvent } from '../../timeline/timeline.models';

interface Field {
  label: string;
  value: string;
  mono?: boolean;
}

/** Every field the query returns for one event, by source (graphql/operations.ts). */
export function detailFields(e: TimelineEvent): Field[] {
  switch (e.source) {
    case 'patch': {
      const p = e.raw as PatchEvent;
      return [
        { label: 'KB', value: p.patch.kbId, mono: true },
        { label: 'Vendor', value: p.patch.vendor },
        { label: 'Severity', value: p.patch.severity },
        { label: 'Status', value: p.status },
        { label: 'Patch ID', value: p.patch.id, mono: true },
        { label: 'Event ID', value: p.id, mono: true },
      ];
    }
    case 'vulnerability': {
      const v = e.raw as VulnerabilityEvent;
      return [
        { label: 'CVE', value: v.cve.id, mono: true },
        { label: 'CVSS score', value: v.cve.cvssScore.toFixed(1) },
        { label: 'Severity', value: v.cve.severity },
        { label: 'Event', value: v.kind },
        { label: 'Finding state', value: v.findingState },
        { label: 'Finding ID', value: v.findingId, mono: true },
        { label: 'Event ID', value: v.id, mono: true },
      ];
    }
    case 'softwareinstall': {
      const i = e.raw as InstallEvent;
      return [
        { label: 'Action', value: i.action },
        { label: 'Software', value: i.software.name },
        { label: 'Version', value: i.software.version, mono: true },
        { label: 'Publisher', value: i.software.publisher },
        { label: 'Result', value: i.result },
        { label: 'Event ID', value: i.id, mono: true },
      ];
    }
  }
}

/** The selected event in full, in its subgraph's colour; a hint while nothing is selected. */
@Component({
  selector: 'app-event-detail',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.data-source]': 'event()?.source ?? null' },
  template: `
    @if (event(); as e) {
      <section class="detail" [style.--source-color]="meta().color" aria-live="polite">
        <header class="head">
          <span class="source"
            ><mat-icon aria-hidden="true">{{ meta().icon }}</mat-icon
            >{{ meta().name }}</span
          >
          <time class="when" [attr.datetime]="e.occurredAt" [matTooltip]="e.occurredAt">
            {{ e.occurredAt | date: 'medium' }}
          </time>
          <button
            mat-icon-button
            type="button"
            class="close"
            aria-label="Close details"
            (click)="close.emit()"
          >
            <mat-icon>close</mat-icon>
          </button>
        </header>
        <h2 class="title">{{ e.title }}</h2>
        <p class="subtitle">{{ e.subtitle }}</p>
        <div class="chips">
          <span class="chip status" [attr.data-status]="e.status">{{ e.status }}</span>
          @if (e.severity) {
            <span class="chip severity" [attr.data-severity]="e.severity">{{ e.severity }}</span>
          }
        </div>
        <dl class="fields">
          @for (f of fields(); track f.label) {
            <div class="field">
              <dt>{{ f.label }}</dt>
              <dd [class.mono]="f.mono">{{ f.value }}</dd>
            </div>
          }
        </dl>
      </section>
    } @else {
      <p class="hint">
        <mat-icon aria-hidden="true">ads_click</mat-icon>
        Select a point on the timeline to see the event's details.
      </p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .detail {
      position: relative;
      padding: 16px 20px 20px 24px;
      border-radius: 14px;
      border: 1px solid color-mix(in srgb, var(--source-color) 30%, transparent);
      background: color-mix(
        in srgb,
        var(--source-color) 5%,
        var(--mat-sys-surface-container-lowest)
      );
      box-shadow: inset 4px 0 0 var(--source-color);
      animation: appear 0.2s ease-out;
    }
    @keyframes appear {
      from {
        opacity: 0;
        transform: translateY(4px);
      }
    }
    .head {
      display: flex;
      align-items: center;
      gap: 12px;
      flex-wrap: wrap;
      padding-right: 44px;
    }
    .source {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 3px 10px 3px 6px;
      border-radius: 999px;
      background: color-mix(in srgb, var(--source-color) 14%, white);
      color: var(--source-color);
      font: var(--mat-sys-label-medium);
      font-weight: 600;
    }
    .source mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .when {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    .close {
      position: absolute;
      top: 8px;
      right: 8px;
    }
    .title {
      margin: 8px 0 0;
      font: var(--mat-sys-title-large);
      overflow-wrap: anywhere;
    }
    .subtitle {
      margin: 2px 0 12px;
      color: var(--mat-sys-on-surface-variant);
      overflow-wrap: anywhere;
    }
    .chips {
      display: flex;
      gap: 6px;
      flex-wrap: wrap;
    }
    .fields {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
      gap: 12px 24px;
      margin: 16px 0 0;
    }
    dt {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-label-medium);
    }
    dd {
      margin: 2px 0 0;
      font: var(--mat-sys-body-large);
      overflow-wrap: anywhere;
    }
    .hint {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 8px;
      margin: 0;
      padding: 18px 16px;
      border-radius: 14px;
      border: 1px dashed var(--mat-sys-outline-variant);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class EventDetailComponent {
  readonly event = input.required<TimelineEvent | null>();
  readonly close = output<void>();

  protected readonly meta = computed(() => sourceMeta(this.event()!.source));
  protected readonly fields = computed(() => {
    const e = this.event();
    return e ? detailFields(e) : [];
  });
}
