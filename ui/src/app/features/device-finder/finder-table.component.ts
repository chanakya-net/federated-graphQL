import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';

import { PAGE_SIZE, type Category } from '../../finder/finder.models';
import type { Device, DeviceSearchEvent } from '../../graphql/types';
import { providerMeta } from '../../finder/finder.models';
import type { SearchCapability } from '../../graphql/types';

/** One server-selected device and its events grouped into display cells. */
export interface FinderRow {
  device: Device;
  cells: Partial<Record<Category, DeviceSearchEvent[]>>;
}

/**
 * The combined result: one row per device, one column per category in the expression. A cell lists the
 * device's events for that category's filters as chips, or "—" when it has none (possible with OR).
 * Rows open the device's timeline.
 */
@Component({
  selector: 'app-finder-table',
  imports: [
    DatePipe,
    DecimalPipe,
    RouterLink,
    MatIconModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatTableModule,
    MatTooltipModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel table-wrap" aria-label="Matching devices">
      <header class="head">
        <h2 class="title">Devices</h2>
        <span class="summary">
          @if (loading()) {
            Searching…
          } @else {
            {{ total() | number }} device{{ total() === 1 ? '' : 's' }} match the expression
          }
        </span>
      </header>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" class="progress" />
      }
      @if (loading()) {
        <div class="skeleton" aria-busy="true" aria-label="Searching devices">
          @for (row of skeletonRows; track row) {
            <div class="skeleton-row">
              @for (cell of columns(); track cell) {
                <span class="skeleton-bar"></span>
              }
            </div>
          }
        </div>
      } @else if (rows().length === 0) {
        <p class="empty">
          @if (total() > 0) {
            No devices on this page. Choose a previous page.
          } @else {
            No device in {{ tenant() }} matches the expression.
          }
        </p>
      } @else {
        <table mat-table [dataSource]="rows()" class="devices" aria-label="Matching devices">
          <ng-container matColumnDef="device">
            <th mat-header-cell *matHeaderCellDef>Device</th>
            <td mat-cell *matCellDef="let r" class="device-cell">
              <a
                [routerLink]="['/devices', r.device.id]"
                class="id"
                (click)="$event.stopPropagation()"
                >{{ r.device.id }}</a
              >
              @if (r.device.hostname) {
                <span class="host">{{ r.device.hostname }}</span>
                <span class="os">{{ r.device.os }}</span>
              }
            </td>
          </ng-container>
          @for (c of categories(); track c) {
            @let meta = metaOf(c);
            <ng-container [matColumnDef]="'provider-' + c">
              <th mat-header-cell *matHeaderCellDef [style.--source-color]="meta.color">
                <mat-icon class="head-icon" aria-hidden="true">{{ meta.icon }}</mat-icon>
                {{ meta.name }}
              </th>
              <td mat-cell *matCellDef="let r" class="matches" [style.--source-color]="meta.color">
                @if (!r.cells[c] || r.cells[c].length === 0) {
                  <span class="none" aria-label="No match">—</span>
                } @else {
                  <span class="match-list">
                    @for (e of r.cells[c]; track e.id) {
                      <span
                        class="match"
                        [matTooltip]="
                          e.title + (e.occurredAt ? ' · ' + (e.occurredAt | date: 'medium') : '')
                        "
                      >
                        <span class="match-label">{{ e.label }}</span>
                        <span class="chip status" [attr.data-status]="e.status">{{
                          e.status
                        }}</span>
                        @if (e.occurredAt) {
                          <time class="match-when" [attr.datetime]="e.occurredAt">{{
                            e.occurredAt | date: 'mediumDate'
                          }}</time>
                        }
                      </span>
                    }
                  </span>
                }
              </td>
            </ng-container>
          }
          <tr mat-header-row *matHeaderRowDef="columns()"></tr>
          <tr
            mat-row
            *matRowDef="let r; columns: columns()"
            class="row"
            (click)="open(r.device)"
          ></tr>
        </table>
      }
      @if (!loading() && (total() > 0 || pageIndex() > 0)) {
        <mat-paginator
          [length]="total()"
          [pageSize]="pageSize"
          [pageIndex]="pageIndex()"
          [hidePageSize]="true"
          [showFirstLastButtons]="true"
          (page)="onPage($event)"
          aria-label="Result pages"
        />
      }
    </section>
  `,
  styleUrl: './finder-table.component.scss',
})
export class FinderTableComponent {
  private readonly router = inject(Router);

  readonly rows = input.required<readonly FinderRow[]>();
  /** The categories in the expression, in order: one column each. */
  readonly categories = input.required<readonly Category[]>();
  readonly total = input(0);
  readonly pageIndex = input(0);
  /** The server search is still loading. */
  readonly loading = input(false);
  readonly tenant = input('this tenant');
  readonly page = output<number>();

  protected readonly pageSize = PAGE_SIZE;
  protected readonly skeletonRows = Array.from({ length: 5 }, (_, i) => i);
  readonly capabilities = input<readonly SearchCapability[]>([]);
  protected readonly metaOf = (category: string) => providerMeta(this.capabilities(), category);
  protected readonly columns = computed(() => [
    'device',
    ...this.categories().map((category) => `provider-${category}`),
  ]);

  protected onPage(event: PageEvent): void {
    this.page.emit(event.pageIndex);
  }

  protected open(device: FinderRow['device']): void {
    void this.router.navigate(['/devices', device.id]);
  }
}
