import { Component, signal, OnDestroy, OnInit, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { SignalRService } from './core/services/signalr.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App implements OnInit, OnDestroy {
  private readonly msalService = inject(MsalService);
  private readonly signalRService = inject(SignalRService);
  protected readonly title = signal('Frontend');
  private visibilityHandler: (() => void) | null = null;

  ngOnInit(): void {
    // Handle redirect promise when returning from Microsoft login
    this.msalService.instance.handleRedirectPromise().then((response) => {
      if (response) {
        console.log('Redirect response received:', response);
        this.msalService.instance.setActiveAccount(response.account);
      }
      this.ensureSignalRConnection();
    }).catch((error) => {
      console.error('Error handling redirect:', error);
    });

    const handler = () => {
      if (document.visibilityState === 'visible') {
        this.ensureSignalRConnection();
      }
    };
    document.addEventListener('visibilitychange', handler);
    this.visibilityHandler = () => document.removeEventListener('visibilitychange', handler);
    this.ensureSignalRConnection();
  }

  ngOnDestroy(): void {
    this.visibilityHandler?.();
  }

  private ensureSignalRConnection(): void {
    const token = localStorage.getItem('mi_accessToken') ?? '';
    const email = localStorage.getItem('mi_userEmail') ?? '';

    if (!token || !email) {
      this.signalRService.stopConnection();
      return;
    }

    if (!this.signalRService.isConnected) {
      this.signalRService.startConnection(token, email);
    }
  }
}
