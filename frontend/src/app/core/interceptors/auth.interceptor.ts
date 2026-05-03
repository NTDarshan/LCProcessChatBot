import { HttpHandlerFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { MsalService } from '@azure/msal-angular';
import { from, switchMap, catchError, throwError } from 'rxjs';
import { Router } from '@angular/router';

export function authInterceptor(req: HttpRequest<unknown>, next: HttpHandlerFn) {
  const msal = inject(MsalService);
  const router = inject(Router);

  const account = msal.instance.getActiveAccount();
  const storedToken = localStorage.getItem('mi_accessToken');

  if (!account || !storedToken) {
    return next(req);
  }

  // Try silent token acquisition first
  return from(
    msal.instance.acquireTokenSilent({
      scopes: ['User.Read'],
      account,
    })
  ).pipe(
    switchMap((result) => {
      const cloned = req.clone({
        setHeaders: { Authorization: `Bearer ${result.idToken}` },
      });
      return next(cloned);
    }),
    catchError((err) => {
      if (err?.status === 401) {
        router.navigate(['/']);
      }
      return throwError(() => err);
    })
  );
}
