import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterOutlet } from '@angular/router';

import { SessionService } from './core/session.service';
import { UserSwitchComponent } from './features/user-switch/user-switch.component';
import { StateCardComponent } from './shared/state-card.component';

@Component({
  selector: 'app-root',
  imports: [
    RouterLink,
    RouterOutlet,
    MatButtonModule,
    MatIconModule,
    MatToolbarModule,
    StateCardComponent,
    UserSwitchComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-toolbar class="toolbar">
      <a routerLink="/" class="brand">
        <mat-icon aria-hidden="true">timeline</mat-icon>
        <span>SoR Device Timeline</span>
      </a>
      <span class="spacer"></span>
      @if (session.selected()) {
        <app-user-switch />
      }
    </mat-toolbar>
    <main>
      @if (session.loadError(); as error) {
        <app-state-card
          heading="Demo users unavailable"
          message="The UI picks a user from /tokens.json, which token-generator writes when the stack starts. Without it no request can be authenticated."
          [detail]="error"
          icon="group_off"
        >
          <button mat-flat-button (click)="session.load()">Retry</button>
        </app-state-card>
      } @else {
        <router-outlet />
      }
    </main>
  `,
  styles: `
    .toolbar {
      position: sticky;
      top: 0;
      z-index: 2;
      gap: 16px;
      height: 72px;
      background: var(--mat-sys-surface-container);
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }
    .brand {
      display: flex;
      align-items: center;
      gap: 8px;
      color: inherit;
      text-decoration: none;
      font: var(--mat-sys-title-large);
    }
    .brand mat-icon {
      color: var(--mat-sys-primary);
    }
    .spacer {
      flex: 1;
    }
  `,
})
export class App {
  protected readonly session = inject(SessionService);
}
