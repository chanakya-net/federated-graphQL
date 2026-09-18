import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';

import { SessionService } from '../../core/session.service';

/** Toolbar select of the demo users from `/tokens.json`: name, tenant, and one chip per service. */
@Component({
  selector: 'app-user-switch',
  imports: [MatFormFieldModule, MatSelectModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field appearance="outline" subscriptSizing="dynamic" class="field">
      <mat-label>Demo user</mat-label>
      <mat-select
        [value]="session.selected()?.sub"
        (valueChange)="session.select($event)"
        panelWidth=""
        aria-label="Demo user"
      >
        <mat-select-trigger>
          @if (session.selected(); as user) {
            <span class="name">{{ user.name }}</span>
            <span class="chip tenant">{{ user.tenantId }}</span>
          }
        </mat-select-trigger>
        @for (user of session.users(); track user.sub) {
          <mat-option [value]="user.sub">
            <span class="option">
              <span class="name">{{ user.name }}</span>
              <span class="chip tenant">{{ user.tenantId }}</span>
              @for (service of user.services; track service) {
                <span class="chip service">{{ service }}</span>
              } @empty {
                <span class="chip none">no services</span>
              }
            </span>
          </mat-option>
        }
      </mat-select>
    </mat-form-field>
  `,
  styles: `
    .field {
      --mat-form-field-container-height: 44px;
      --mat-form-field-container-vertical-padding: 10px;
      width: 280px;
      max-width: calc(100vw - 88px);
      font: var(--mat-sys-body-medium);
    }
    .option {
      display: inline-flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 6px;
    }
    .name {
      margin-right: 4px;
    }
    .chip {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 8px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
    .chip.tenant {
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }
    .chip.none {
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class UserSwitchComponent {
  protected readonly session = inject(SessionService);
}
