import { inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { EventMessage, EventType, InteractionStatus, AccountInfo, AuthenticationResult } from '@azure/msal-browser';
import { filter } from 'rxjs/operators';
import { environment } from '../../environments/environment';
import { APP_ROUTES } from '../core/models/app-routes';

@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly userName = signal<string | null>(null);
  readonly userEmail = signal<string | null>(null);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  readonly isAuthenticated = signal<boolean>(false);
  private isProcessingLogin = false;
  private refreshTimer: any = null;

  constructor(
    private msal: MsalService, 
    private msalBroadcast: MsalBroadcastService, 
    private router: Router
  ) {
    this.msalBroadcast.msalSubject$
      .pipe(filter((msg: EventMessage) => msg.eventType === EventType.LOGIN_SUCCESS))
      .subscribe((result: EventMessage) => {
        const payload = result.payload as AuthenticationResult;
        const account = payload.account;
        if (account) {
          console.log('LOGIN_SUCCESS event received, processing authentication...');
          this.processSuccessfulMsalLogin(account, payload.idToken!);
        } else {
          console.error('LOGIN_SUCCESS but no account found');
          this.handleAuthenticationError('No account found after login');
        }
      });

    this.msalBroadcast.msalSubject$
      .pipe(filter((msg: EventMessage) => msg.eventType === EventType.LOGIN_FAILURE))
      .subscribe((result) => {
        console.error('LOGIN_FAILURE event:', result.error);
        this.handleAuthenticationError('Login failed. Please try again.');
      });

    this.msalBroadcast.msalSubject$
      .pipe(filter((msg: EventMessage) => msg.eventType === EventType.LOGOUT_SUCCESS))
      .subscribe(() => {
        console.log('LOGOUT_SUCCESS event received');
        this.completeLogout();
      });

    this.msalBroadcast.inProgress$
      .pipe(filter((status: InteractionStatus) => status === InteractionStatus.None))
      .subscribe(() => {
        const account = this.getAccount();
        if (account) {
          this.msal.instance.setActiveAccount(account);
          this.setAccount(account);
          this.startTokenRefreshTimer(account);
        }
      });
  }

  private startTokenRefreshTimer(account: AccountInfo): void {
    if (this.refreshTimer) {
      clearInterval(this.refreshTimer);
    }
    
    // Refresh token every 20 minutes (20 * 60 * 1000 = 1200000 ms)
    const refreshInterval = 20 * 60 * 1000;
    
    this.refreshTimer = setInterval(() => {
      this.msal.instance.acquireTokenSilent({
        scopes: ['User.Read'],
        account: account,
        forceRefresh: true
      }).then(result => {
        if (result.idToken) {
          localStorage.setItem('mi_accessToken', result.idToken);
          console.log('Access token successfully refreshed automatically.');
        }
      }).catch(err => {
        console.error('Failed to refresh token silently', err);
      });
    }, refreshInterval);
  }

  login(): void {
    if (this.isProcessingLogin) {
      return;
    }

    const activeAccount = this.msal.instance.getActiveAccount();
    const storedEmail = localStorage.getItem('mi_userEmail');
    const storedToken = localStorage.getItem('mi_accessToken');

    if (activeAccount && storedEmail && storedToken) {
      this.setAccount(activeAccount);
      this.isAuthenticated.set(true);
      this.router.navigate([APP_ROUTES.dashboard]);
      return;
    }

    this.initiateFullLogin();
  }

  private processSuccessfulMsalLogin(account: AccountInfo, accessToken: string): void {
    if (this.isProcessingLogin) {
      return;
    }

    const userEmail = account.username || account.localAccountId;
    
    if (!userEmail) {
      this.handleAuthenticationError('Unable to retrieve user email from Microsoft account');
      return;
    }

    this.isProcessingLogin = true;
    this.loading.set(true);
    this.msal.instance.setActiveAccount(account);
    this.setAccount(account);

    localStorage.setItem('mi_accessToken', accessToken);
    localStorage.setItem('mi_userEmail', userEmail);
    
    this.isProcessingLogin = false;
    this.loading.set(false);
    this.error.set(null);
    this.isAuthenticated.set(true);
    this.router.navigate([APP_ROUTES.dashboard]);
  }

  private initiateFullLogin(): void {
    if (this.loading()) {
      return;
    }

    this.isProcessingLogin = true;
    this.loading.set(true);
    this.error.set(null);

    this.msal.loginRedirect({
      scopes: ['User.Read'],
      redirectUri: environment.redirectURL,
    });
  }

  private handleAuthenticationError(errorMessage: string): void {
    this.loading.set(false);
    this.error.set(errorMessage);
    this.isAuthenticated.set(false);
    this.isProcessingLogin = false;
    this.clearUserData();
  }

  private clearUserData(): void {
    localStorage.removeItem('mi_userEmail');
    localStorage.removeItem('mi_accessToken');
    this.userName.set(null);
    this.userEmail.set(null);
    if (this.refreshTimer) {
      clearInterval(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  logout(): void {
    this.loading.set(true);
    this.clearUserData();
    this.isAuthenticated.set(false);
    
    this.msal.logoutRedirect({ 
      postLogoutRedirectUri: environment.postLogoutRedirectUri 
    }).subscribe({
      next: () => {
        // Logged out
      },
      error: (e) => {
        this.loading.set(false);
      }
    });
  }

  isSessionValid(): boolean {
    const hasAccount = !!this.getAccount();
    const hasEmail = !!localStorage.getItem('mi_userEmail');
    const hasToken = !!localStorage.getItem('mi_accessToken');
    
    return hasAccount && hasEmail && hasToken;
  }

  private getAccount(): AccountInfo | null {
    const accounts = this.msal.instance.getAllAccounts();
    return accounts.length > 0 ? accounts[0] : null;
  }

  private setAccount(account: AccountInfo): void {
    const email = account.username ?? null;
    const name = account.name ?? null;
    
    if (email) {
      localStorage.setItem('mi_userEmail', email);
    }
    
    this.userEmail.set(email);
    this.userName.set(name);
  }

  private completeLogout(): void {
    this.clearUserData();
    this.isAuthenticated.set(false);
    this.isProcessingLogin = false;
  }
}
