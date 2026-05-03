import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { InteractionStatus } from '@azure/msal-browser';
import { filter, Subject, takeUntil } from 'rxjs';
import { APP_ROUTES } from '../core/models/app-routes';

@Component({
  selector: 'app-auth-callback',
  imports: [CommonModule],
  template: `
    <main class="callback-shell">
      <div class="status-card" aria-live="polite">
        <div class="spinner" aria-hidden="true"></div>
        <h1>Signing you in</h1>
        <p>{{ message() }}</p>
      </div>
    </main>
  `,
  styles: [`
    .callback-shell {
      min-height: 100vh;
      display: grid;
      place-items: center;
      padding: 24px;
      background: #f6f9fc;
    }

    .status-card {
      width: min(420px, 100%);
      padding: 32px;
      border-radius: 20px;
      background: #ffffff;
      box-shadow: 0 20px 45px rgba(15, 23, 42, 0.12);
      text-align: center;
      color: #163c73;
    }

    .status-card h1 {
      margin: 0 0 12px;
      font-size: 1.6rem;
      font-weight: 700;
    }

    .status-card p {
      margin: 0;
      color: #5d769c;
    }

    .spinner {
      width: 46px;
      height: 46px;
      margin: 0 auto 20px;
      border: 4px solid #dbe8f7;
      border-top-color: #1f5ea8;
      border-radius: 50%;
      animation: spin 0.8s linear infinite;
    }

    @keyframes spin {
      to {
        transform: rotate(360deg);
      }
    }
  `]
})
export class AuthCallbackComponent implements OnInit, OnDestroy {
  private readonly msal = inject(MsalService);
  private readonly msalBroadcast = inject(MsalBroadcastService);
  private readonly router = inject(Router);
  private readonly destroy$ = new Subject<void>();

  readonly message = signal('Checking your Microsoft session...');

  ngOnInit(): void {
    this.msalBroadcast.inProgress$
      .pipe(
        filter((status: InteractionStatus) => status === InteractionStatus.None),
        takeUntil(this.destroy$)
      )
      .subscribe(() => {
        this.finishRedirect();
      });

    setTimeout(() => {
      this.finishRedirect();
    }, 800);
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  private finishRedirect(): void {
    const account = this.msal.instance.getActiveAccount() ?? this.msal.instance.getAllAccounts()[0];

    if (account) {
      this.msal.instance.setActiveAccount(account);
      this.message.set('Session detected. Redirecting...');
      void this.router.navigateByUrl(APP_ROUTES.dashboard, { replaceUrl: true });
      return;
    }

    this.message.set('No active session found. Returning to sign-in...');
    void this.router.navigateByUrl(APP_ROUTES.landing, { replaceUrl: true });
  }
}
