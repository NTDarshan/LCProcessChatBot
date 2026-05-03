import { Injectable } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  HttpTransportType,
  IRetryPolicy,
  LogLevel,
  RetryContext,
} from '@microsoft/signalr';
import { environment } from '../../../environments/environment';
import { BehaviorSubject, Subject } from 'rxjs';
import { ProcessingStageUpdate } from '../../shared/models/chat.models';

@Injectable({ providedIn: 'root' })
export class SignalRService {
  connection: HubConnection | null = null;

  readonly connectionState$ = new BehaviorSubject<string>('Disconnected');
  readonly chatChunk$ = new Subject<string>();
  readonly chatComplete$ = new Subject<any>();
  readonly chatError$ = new Subject<string>();
  readonly processingStage$ = new Subject<ProcessingStageUpdate>();

  private userEmail: string | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;

  async startConnection(token: string, userEmail: string): Promise<void> {
    this.userEmail = userEmail;

    if (this.connection?.state === HubConnectionState.Connected) {
      await this.joinUserGroup();
      return;
    }

    if (!token) {
      this.connectionState$.next('Failed');
      return;
    }

    const retryPolicy: IRetryPolicy = {
      nextRetryDelayInMilliseconds: (ctx: RetryContext) => {
        const attempt = ctx.previousRetryCount + 1;
        if (attempt <= 3) return 2000;
        if (attempt <= 5) return 5000;
        return 10000;
      },
    };

    this.connection = new HubConnectionBuilder()
      .withUrl(`${environment.signalRUrl}/hubs/chat`, {
        accessTokenFactory: () => localStorage.getItem('mi_accessToken') || token,
        transport: HttpTransportType.WebSockets,
        skipNegotiation: true,
      })
      .withAutomaticReconnect(retryPolicy)
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.on('ReceiveChunk', (chunk: string) => {
      this.chatChunk$.next(chunk);
    });

    this.connection.on('MessageComplete', (dto: any) => {
      this.chatComplete$.next(dto);
    });

    this.connection.on('MessageError', (text: string) => {
      this.chatError$.next(text);
    });

    this.connection.on('ProcessingStage', (stage: ProcessingStageUpdate) => {
      this.processingStage$.next(stage);
    });

    this.connection.onreconnecting(() => {
      this.connectionState$.next('Reconnecting');
    });

    this.connection.onreconnected(async () => {
      this.connectionState$.next('Connected');
      await this.joinUserGroup();
    });

    this.connection.onclose(() => {
      this.connectionState$.next('Disconnected');
    });

    try {
      await this.connection.start();
      this.connectionState$.next('Connected');
      await this.joinUserGroup();
    } catch {
      this.connectionState$.next('Failed');
      if (this.retryTimer) clearTimeout(this.retryTimer);
      this.retryTimer = setTimeout(() => {
        this.startConnection(token, userEmail);
      }, 5000);
    }
  }

  async stopConnection(): Promise<void> {
    if (this.retryTimer) {
      clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }

    if (this.connection) {
      await this.connection.stop();
    }

    this.connection = null;
    this.connectionState$.next('Disconnected');
  }

  async sendMessage(userEmail: string, message: string, sessionId?: string): Promise<void> {
    if (this.connection?.state !== HubConnectionState.Connected) {
      throw new Error('SignalR is not connected');
    }

    await this.connection.invoke('SendMessage', userEmail, message, sessionId ?? null);
  }

  get isConnected(): boolean {
    return this.connection?.state === HubConnectionState.Connected;
  }

  private async joinUserGroup(): Promise<void> {
    if (!this.userEmail) return;
    if (this.connection?.state !== HubConnectionState.Connected) return;
    await this.connection.invoke('JoinUserGroup', this.userEmail);
  }
}
