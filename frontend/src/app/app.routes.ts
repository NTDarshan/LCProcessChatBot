import { Routes } from '@angular/router';
import { LandingComponent } from './landing.component/landing.component';
import { AuthCallbackComponent } from './auth-callback.component/auth-callback.component';
import { APP_ROUTE_SEGMENTS } from './core/models/app-routes';
import { MsalGuard } from '@azure/msal-angular';

export const routes: Routes = [
  { path: '', component: LandingComponent },
  { path: APP_ROUTE_SEGMENTS.home, component: AuthCallbackComponent },
  {
    path: APP_ROUTE_SEGMENTS.dashboard,
    loadComponent: () =>
      import('./dashboard.component/dashboard.component').then(
        (m) => m.DashboardComponent
      ),
    canActivate: [MsalGuard],
  },
  { path: '**', redirectTo: '' },
];
