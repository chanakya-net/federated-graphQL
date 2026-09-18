import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Devices · SoR',
    loadComponent: () =>
      import('./features/device-search/device-search.page').then((m) => m.DeviceSearchPage),
  },
  {
    path: 'devices/:id',
    title: 'Device timeline · SoR',
    loadComponent: () =>
      import('./features/device-timeline/device-timeline.page').then((m) => m.DeviceTimelinePage),
  },
  { path: '**', redirectTo: '' },
];
