import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import devUsers from '../../../testing/tokens.dev.json';
import type { DemoUser } from '../../core/demo-user';
import { SessionService } from '../../core/session.service';
import { UserSwitchComponent } from './user-switch.component';

describe('UserSwitchComponent', () => {
  const users = devUsers as DemoUser[];
  const selected = signal<DemoUser | null>(users[1]);
  const select = vi.fn();

  beforeEach(() => {
    select.mockReset();
    TestBed.configureTestingModule({
      providers: [
        { provide: SessionService, useValue: { users: signal(users), selected, select } },
      ],
    });
  });

  async function openPanel(): Promise<HTMLElement[]> {
    const fixture = TestBed.createComponent(UserSwitchComponent);
    await fixture.whenStable();
    (fixture.nativeElement.querySelector('.mat-mdc-select-trigger') as HTMLElement).click();
    await fixture.whenStable();
    return [...document.querySelectorAll<HTMLElement>('mat-option')];
  }

  it('shows the selected user and tenant in the trigger', async () => {
    const fixture = TestBed.createComponent(UserSwitchComponent);
    await fixture.whenStable();
    const trigger = fixture.nativeElement.querySelector('mat-select-trigger') as HTMLElement;
    expect(trigger.querySelector('.name')?.textContent).toBe('Bob (Tenant A)');
    expect(trigger.querySelector('.tenant')?.textContent).toBe('TenantA');
  });

  it('lists every user with a tenant chip and one chip per service', async () => {
    const options = await openPanel();
    expect(options).toHaveLength(5);
    const carol = options[2];
    expect(carol.querySelector('.name')?.textContent).toBe('Carol (Tenant A)');
    expect(carol.querySelector('.chip.tenant')?.textContent).toBe('TenantA');
    expect([...carol.querySelectorAll('.chip.service')].map((c) => c.textContent)).toEqual([
      'softwareinstall',
    ]);
    expect([...options[0].querySelectorAll('.chip.service')].map((c) => c.textContent)).toEqual([
      'patch',
      'vulnerability',
      'softwareinstall',
    ]);
  });

  it('selecting a user switches the session', async () => {
    const options = await openPanel();
    options[3].click();
    expect(select).toHaveBeenCalledWith('dave');
  });
});
