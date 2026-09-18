import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';

/**
 * A page-level state (error, not found) shown instead of content, so no state ends in a blank page.
 * Action buttons go in the content slot.
 */
@Component({
  selector: 'app-state-card',
  imports: [MatCardModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card
      appearance="outlined"
      class="state-card"
      [class]="tone()"
      [attr.role]="tone() === 'error' ? 'alert' : 'status'"
    >
      <mat-card-content class="body">
        <mat-icon class="icon" aria-hidden="true">{{ icon() }}</mat-icon>
        <div class="text">
          <h2 class="title">{{ heading() }}</h2>
          <p class="message">{{ message() }}</p>
          @if (detail()) {
            <p class="detail">{{ detail() }}</p>
          }
          <div class="actions"><ng-content /></div>
        </div>
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    .state-card {
      max-width: 720px;
      margin: 24px auto;
    }
    .body {
      display: flex;
      gap: 16px;
      align-items: flex-start;
    }
    .icon {
      flex: none;
      width: 40px;
      height: 40px;
      font-size: 40px;
    }
    .error .icon {
      color: var(--mat-sys-error);
    }
    .info .icon {
      color: var(--mat-sys-on-surface-variant);
    }
    .title {
      margin: 4px 0 8px;
      font: var(--mat-sys-title-large);
    }
    .message {
      margin: 0 0 8px;
    }
    .detail {
      margin: 0 0 8px;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
    .actions {
      display: flex;
      gap: 8px;
      margin-top: 8px;
    }
    .actions:empty {
      display: none;
    }
  `,
})
export class StateCardComponent {
  readonly tone = input<'error' | 'info'>('error');
  readonly icon = input('error');
  readonly heading = input.required<string>();
  readonly message = input.required<string>();
  readonly detail = input<string | null>(null);
}
