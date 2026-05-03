import { Component, computed, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { MsalService, MsalBroadcastService } from '@azure/msal-angular';
import { InteractionStatus } from '@azure/msal-browser';
import { filter, Subject, takeUntil } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { APP_ROUTES } from '../core/models/app-routes';

@Component({
  selector: 'app-landing',
  imports: [CommonModule],
  templateUrl: './landing.component.html',
  styleUrl: './landing.component.scss'
})
export class LandingComponent implements OnInit, OnDestroy {
  private readonly auth = inject(AuthService);
  private readonly msal = inject(MsalService);
  private readonly msalBroadcast = inject(MsalBroadcastService);
  private readonly router = inject(Router);
  private readonly destroy$ = new Subject<void>();
  
  readonly loading = this.auth.loading;
  readonly error = this.auth.error;
  readonly disabled = computed(() => this.loading());
  readonly checkingAuth = signal<boolean>(true);

  ngOnInit(): void {
    if (this.hasCompleteValidSession()) {
      this.checkingAuth.set(false);
      this.router.navigate([APP_ROUTES.dashboard]);
      return;
    }

    this.msalBroadcast.inProgress$
      .pipe(
        filter((status: InteractionStatus) => status === InteractionStatus.None),
        takeUntil(this.destroy$)
      )
      .subscribe(() => {
        this.handleAuthenticationCheck();
      });

    setTimeout(() => {
      if (this.checkingAuth()) {
        this.handleAuthenticationCheck();
      }
    }, 500);
  }

  private hasCompleteValidSession(): boolean {
    const activeAccount = this.msal.instance.getActiveAccount();
    const storedEmail = localStorage.getItem('mi_userEmail');
    const storedToken = localStorage.getItem('mi_accessToken');
    
    return !!(activeAccount && storedEmail && storedToken);
  }

  private handleAuthenticationCheck(): void {
    if (this.hasCompleteValidSession()) {
      console.log('✓ Complete session found after MSAL interaction');
      this.checkingAuth.set(false);
      this.router.navigate([APP_ROUTES.dashboard]);
      return;
    }

    const accounts = this.msal.instance.getAllAccounts();
    if (accounts.length > 0) {
      console.log('MSAL account found but session incomplete, will complete on login click');
      this.msal.instance.setActiveAccount(accounts[0]);
      this.checkingAuth.set(false);
      return;
    }

    console.log('No session found, showing landing page');
    this.checkingAuth.set(false);
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  login(): void {
    console.log('Login button clicked');
    this.checkingAuth.set(true);
    this.auth.login();
  }
}
