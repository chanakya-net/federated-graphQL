import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Devices · SoR',
    loadComponent: () =>
      import('./features/device-search/device-search.page').then((m) => m.DeviceSearchPage),
  },
  {
    path: 'find',
    title: 'Find devices · SoR',
    loadComponent: () =>
      import('./features/device-finder/device-finder.page').then((m) => m.DeviceFinderPage),
  },
  {
    path: 'devices/:id',
    title: 'Device timeline · SoR',
    loadComponent: () =>
      import('./features/device-timeline/device-timeline.page').then((m) => m.DeviceTimelinePage),
  },
  { path: '**', redirectTo: '' },
];
