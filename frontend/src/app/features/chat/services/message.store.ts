import {
  signal, computed, effect, inject, Injectable, OnDestroy
} from '@angular/core';
import { Subject, Subscription } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import {
  Message,
  ChatSession,
  ChatHistoryItem,
  ChatResponse,
  ProcessingStageUpdate
} from '../../../shared/models/chat.models';
import { ApiService } from '../../../core/services/api.service';
import { SignalRService } from '../../../core/services/signalr.service';

@Injectable({ providedIn: 'root' })
export class MessageStore implements OnDestroy {
  private readonly api     = inject(ApiService);
  private readonly signalR = inject(SignalRService);

  // ── Destroy signal for subscription cleanup ──────────────────────────────
  // Emits once when the service is destroyed so all subscriptions are cleaned up.
  private readonly destroy$ = new Subject<void>();

  // ── Core state ──────────────────────────────────────────────────────────────
  readonly messages            = signal<Message[]>([]);
  readonly isTyping            = signal<boolean>(false);
  readonly isStreaming         = signal<boolean>(false);
  readonly streamingText       = signal<string>('');
  readonly streamingMessageIdx = signal<number>(-1);
  readonly processingStages    = signal<ProcessingStageUpdate[]>([]);
  readonly processingProgress  = signal<number>(0);
  readonly processingSummary   = signal<string>('Preparing LC request...');
  readonly liveStatusLabel     = signal<string>('Reviewing LC Context...');
  readonly sessionId           = signal<string | null>(null);
  readonly sessions            = signal<ChatSession[]>([]);
  readonly showRightPanel      = signal<boolean>(false);
  readonly isLoadingSessions   = signal<boolean>(false);
  readonly isLoadingHistory    = signal<boolean>(false);
  // Raw DB rows from the most recent assistant response – drives the right panel
  readonly lastResponseData    = signal<object[]>([]);

  readonly connectionStatus = signal<'connected' | 'connecting' | 'disconnected'>('disconnected');

  // ── Computed ─────────────────────────────────────────────────────────────────
  readonly lastMessage = computed(() => {
    const msgs = this.messages();
    return msgs[msgs.length - 1] ?? null;
  });

  readonly hasMessages = computed(() => this.messages().length > 0);

  // ── Auto-scroll ───────────────────────────────────────────────────────────────
  constructor() {
    effect(() => {
      const _ = this.lastMessage(); // track dependency
      this.scrollToBottom();
    });

    // ── Wire up SignalR observables with takeUntil for automatic cleanup ─────
    this.signalR.connectionState$
      .pipe(takeUntil(this.destroy$))
      .subscribe((state) => {
        if (state === 'Connected') this.connectionStatus.set('connected');
        else if (state === 'Reconnecting') this.connectionStatus.set('connecting');
        else this.connectionStatus.set('disconnected');
      });

    this.signalR.chatChunk$
      .pipe(takeUntil(this.destroy$))
      .subscribe((chunk) => {
        // ── Stale-chunk guard ──────────────────────────────────────────────
        // If isStreaming is false it means stopGeneration() was called;
        // discard any chunks that arrive after the stop was requested.
        if (!this.isStreaming()) return;

        const idx = this.streamingMessageIdx();
        if (idx < 0) return;

        // Hide loader immediately when first response chunk arrives.
        this.isTyping.set(false);
        this.processingStages.set([]);
        this.processingProgress.set(0);
        this.processingSummary.set('Preparing LC request...');
        this.liveStatusLabel.set('Reviewing LC Context...');

        this.streamingText.update((t) => t + chunk);
        const text = this.streamingText();

        this.messages.update((list) => {
          if (idx < 0 || idx >= list.length) return list;
          const copy    = [...list];
          const current = copy[idx];
          copy[idx] = {
            ...current,
            content: text,
            responseType: 'streaming',
            isStreaming: true,
          };
          return copy;
        });

        this.scrollToBottom();
      });

    this.signalR.chatComplete$
      .pipe(takeUntil(this.destroy$))
      .subscribe((dto: ChatResponse) => {
        // Discard completion events that arrive after stopGeneration()
        if (!this.isStreaming()) return;

        const idx = this.streamingMessageIdx();
        if (idx < 0) return;

        if (dto.sessionId) this.sessionId.set(dto.sessionId);

        // Defer chart/table rendering to the next animation frame.
        // This prevents the chart library's synchronous rendering from blocking
        // the event loop and causing the 1-2 second delay users perceive.
        // The streamed text is already on screen; deferring the data allows
        // the browser to stabilize before expensive chart rendering starts.
        requestAnimationFrame(() => {
          this.messages.update((list) => {
            if (idx < 0 || idx >= list.length) return list;
            const copy    = [...list];
            const current = copy[idx];
            copy[idx] = {
              id: current.id,
              role: 'assistant',
              content: dto.response ?? '',
              timestamp: new Date(),
              executedQuery: dto.executedQuery,
              showQuery: false,
              data: dto.data as Record<string, unknown>[],
              intent: dto.intent,
              responseType: dto.responseType,
              chartType: dto.chartType ?? null,
              isTextToSql: dto.isTextToSql ?? false,
              queryType: dto.queryType ?? null,
              suggestedQuestions: dto.suggestedQuestions ?? [],
              clarification: dto.clarification ?? undefined,
            };
            return copy;
          });

          this._resetStreamingState();
          this.lastResponseData.set(dto.data ?? []);
          this.showRightPanel.set(true);
          this.scrollToBottom();

          // Defer session loading so chart rendering completes first.
          setTimeout(() => this.loadSessions(), 0);
        });
      });

    this.signalR.chatError$
      .pipe(takeUntil(this.destroy$))
      .subscribe((text) => {
        const idx = this.streamingMessageIdx();
        if (idx < 0) return;

        this.messages.update((list) => {
          if (idx < 0 || idx >= list.length) return list;
          const copy    = [...list];
          const current = copy[idx];
          copy[idx] = {
            ...current,
            content: text,
            responseType: 'text_only',
            isStreaming: false,
          };
          return copy;
        });

        this._resetStreamingState();
        this.liveStatusLabel.set('Failed');
        this.scrollToBottom();
      });

    this.signalR.processingStage$
      .pipe(takeUntil(this.destroy$))
      .subscribe((stage: ProcessingStageUpdate) => {
        if (this.streamingText().length > 0) return;

        this.isTyping.set(true);
        this.processingProgress.set(Math.max(0, Math.min(100, stage.progressPercent ?? 0)));
        this.processingSummary.set(stage.stageName || 'Processing...');
        this.liveStatusLabel.set(
          (stage.liveLabel && stage.liveLabel.trim())
            ? stage.liveLabel
            : this.mapStageToStatusLabel(stage)
        );

        this.processingStages.update((current) => {
          const index = current.findIndex((s) => s.stageKey === stage.stageKey);
          if (index === -1) return [...current, stage];

          const copy = [...current];
          copy[index] = { ...copy[index], ...stage };
          return copy;
        });

        if (stage.status === 'failed') {
          this.isTyping.set(false);
        }
      });
  }

  // ── OnDestroy – clean up all subscriptions ────────────────────────────────
  // Fires when Angular destroys this service (e.g. full app teardown).
  // Also called explicitly by components using ngOnDestroy pattern.
  ngOnDestroy(): void {
    this.stopGeneration();    // abort any in-flight request
    this.destroy$.next();     // complete all takeUntil subscriptions
    this.destroy$.complete();
  }

  // ── stopGeneration ─────────────────────────────────────────────────────────
  // Called by the "Stop Generating" button, component destroy, or before a new
  // message is sent to interrupt any active stream.
  //
  // What this does:
  //  • For the HTTP fallback path: aborts the in-flight fetch via AbortController,
  //    which triggers HttpContext.RequestAborted on the backend.
  //  • For the SignalR path: backend cancellation is handled automatically via
  //    Context.ConnectionAborted; on the frontend we reset state so stale
  //    chunks (if any arrive before the backend stops) are discarded.
  stopGeneration(): void {
    // Abort any active HTTP request – triggers backend RequestAborted token
    this.api.abortActiveRequest();

    // Immediately reset streaming state so stale chunks are discarded
    // (the isStreaming() === false guard in the chunk subscriber handles this)
    this._resetStreamingState();
  }

  // ── Load all sessions for the logged-in user from the DB ─────────────────────
  async loadSessions(): Promise<void> {
    const email = localStorage.getItem('mi_userEmail');
    if (!email) return;

    this.isLoadingSessions.set(true);
    try {
      const sessions = await this.api.getSessions(email);
      // Ensure dates are proper Date objects after JSON parse
      this.sessions.set(
        sessions.map(s => ({ ...s, createdAt: new Date(s.createdAt) }))
      );
    } catch {
      // Non-fatal – sidebar simply shows empty
    } finally {
      this.isLoadingSessions.set(false);
    }
  }

  // ── Load history for a specific session and replay it into the message list ──
  async loadSession(session: ChatSession): Promise<void> {
    const email = localStorage.getItem('mi_userEmail');
    if (!email) return;

    // Stop any active generation before switching sessions
    this.stopGeneration();

    // Reset current conversation state before loading the new session
    this.messages.set([]);
    this.isTyping.set(false);
    this.showRightPanel.set(false);
    this.sessionId.set(session.sessionId);

    this.isLoadingHistory.set(true);
    try {
      const history = await this.api.getSessionHistory(session.sessionId, email);
      // Map backend history items to the local Message shape
      const messages: Message[] = history.map((h: ChatHistoryItem) => {
        let parsedData: Record<string, unknown>[] | undefined;
        if (h.data) {
          try {
            parsedData = JSON.parse(h.data);
          } catch {
            parsedData = undefined;
          }
        }

        return {
          id: crypto.randomUUID(),
          role: h.role,
          content: h.content,
          timestamp: new Date(h.createdAt),
          executedQuery: h.executedQuery,  // restore "View Query" for historical messages
          showQuery: false,
          intent: h.intent,
          responseType: h.responseType,
          queryType: h.queryType ?? null,
          data: parsedData
        };
      });
      this.messages.set(messages);
      if (messages.length > 0) this.showRightPanel.set(true);
    } catch {
      // If history fails, we still switched session – user can continue typing
    } finally {
      this.isLoadingHistory.set(false);
    }
  }

  // ── Send a message via the real backend API ──────────────────────────────────
  async sendMessage(text: string): Promise<void> {
    // If a previous stream is active, stop it before starting a new one.
    // This covers the "new message interruption" scenario and prevents race conditions.
    if (this.isStreaming() || this.isTyping()) {
      this.stopGeneration();
    }

    // Advance the generation counter so stale chunks from the old stream
    // are silently discarded even if they arrive after stopGeneration().
    this.signalR.advanceGeneration();

    // Append the user bubble immediately for instant UI feedback
    const userMsg: Message = {
      id: crypto.randomUUID(),
      role: 'user',
      content: text,
      timestamp: new Date(),
    };
    this.messages.update((m) => [...m, userMsg]);

    // Read the authenticated user's email from localStorage (set by MSAL auth flow)
    const userEmail = localStorage.getItem('mi_userEmail') ?? '';

    try {
      const placeholder: Message = {
        id: crypto.randomUUID(),
        role: 'assistant',
        content: '',
        timestamp: new Date(),
        responseType: 'streaming',
        isStreaming: true,
      };
      this.messages.update((m) => [...m, placeholder]);
      this.streamingMessageIdx.set(this.messages().length - 1);
      this.isStreaming.set(true);
      this.isTyping.set(true);
      this.streamingText.set('');
      this.processingStages.set([]);
      this.processingProgress.set(0);
      this.processingSummary.set('Starting LC pipeline...');
      this.liveStatusLabel.set('Reviewing LC Context...');

      if (this.signalR.isConnected) {
        try {
          // SignalR path: backend cancellation is driven by Context.ConnectionAborted
          await this.signalR.sendMessage(userEmail, text, this.sessionId() ?? undefined);
          return;
        } catch {
          await this.sendMessageHttp(text, userEmail, this.streamingMessageIdx());
          return;
        }
      }

      await this.sendMessageHttp(text, userEmail, this.streamingMessageIdx());
    } catch (err) {
      // If err is a DOMException with name 'AbortError' it means the user
      // clicked stop – this is not a system error, reset state silently.
      if (err instanceof Error && err.name === 'AbortError') {
        this._resetStreamingState();
        return;
      }

      this._resetStreamingState();
      this.liveStatusLabel.set('Failed');
      const errorMsg: Message = {
        id: crypto.randomUUID(),
        role: 'assistant',
        content: 'Something went wrong. Please try again.',
        timestamp: new Date(),
        error: true,
        responseType: 'text_only',
      };
      this.messages.update((m) => [...m, errorMsg]);
    }
  }

  // ── Retry the last user message after an error ───────────────────────────────
  retryLast(): void {
    const msgs     = this.messages();
    const lastUser = [...msgs].reverse().find((m) => m.role === 'user');
    if (lastUser) {
      this.messages.update((m) => m.filter((x) => !x.error));
      this.sendMessage(lastUser.content);
    }
  }

  // ── Delete a session and remove it from the local list ──────────────────────
  async deleteSession(sessionId: string): Promise<void> {
    const email = localStorage.getItem('mi_userEmail');
    if (!email) return;

    try {
      await this.api.deleteSession(sessionId, email);
      // Remove from local signal immediately for instant UI feedback
      this.sessions.update((list) => list.filter((s) => s.sessionId !== sessionId));
      // If the deleted session is the active one, reset to a fresh state
      if (this.sessionId() === sessionId) {
        this.newSession();
      }
    } catch (err) {
      console.error('Failed to delete session', err);
    }
  }

  // ── Start a fresh conversation thread ────────────────────────────────────────
  newSession(): void {
    // Stop any active generation when starting a new session
    this.stopGeneration();
    this.messages.set([]);
    this.showRightPanel.set(false);
    // Clear sessionId so the backend creates a new session on next message
    this.sessionId.set(null);
  }

  // ── Private helpers ───────────────────────────────────────────────────────────
  private appendAssistantMessage(
    content: string,
    executedQuery?: string,
    data?: Record<string, unknown>[],
    intent?: string,
    responseType?: string,
    chartType?: string | null,
    isTextToSql?: boolean,
    queryType?: string | null
  ): void {
    const msg: Message = {
      id: crypto.randomUUID(),
      role: 'assistant',
      content,
      timestamp: new Date(),
      executedQuery,
      showQuery: false,
      data,
      intent,
      responseType,
      chartType: chartType ?? null,
      isTextToSql: isTextToSql ?? false,
      queryType: queryType ?? null
    };
    this.messages.update((m) => [...m, msg]);
  }

  private async sendMessageHttp(text: string, userEmail: string, assistantIdx: number): Promise<void> {
    this.isTyping.set(true);
    this.liveStatusLabel.set('Reviewing LC Context...');
    this.processingSummary.set('HTTP fallback in progress...');
    this.processingProgress.set(20);
    try {
      // api.sendMessage() manages its own AbortController; stopGeneration()
      // calls api.abortActiveRequest() to cancel this in-flight fetch.
      const resp = await this.api.sendMessage({
        message: text,
        userEmail,
        sessionId: this.sessionId() ?? undefined,
      });

      // Guard: if streaming was stopped while the HTTP request was in-flight,
      // discard the response to prevent stale content rendering.
      if (!this.isStreaming()) return;

      if (resp.sessionId) {
        this.sessionId.set(resp.sessionId);
      }

      // Defer chart/table rendering to the next animation frame for instant-feeling UX.
      requestAnimationFrame(() => {
        this.messages.update((list) => {
          if (assistantIdx < 0 || assistantIdx >= list.length) return list;
          const copy    = [...list];
          const current = copy[assistantIdx];
          copy[assistantIdx] = {
            id: current.id,
            role: 'assistant',
            content: resp.response,
            timestamp: new Date(),
            executedQuery: resp.executedQuery,
            showQuery: false,
            data: resp.data as Record<string, unknown>[],
            intent: resp.intent,
            responseType: resp.responseType,
            chartType: resp.chartType ?? null,
            isTextToSql: resp.isTextToSql ?? false,
            queryType: resp.queryType ?? null,
            suggestedQuestions: resp.suggestedQuestions ?? [],
            clarification: resp.clarification ?? undefined,
          };
          return copy;
        });

        this.lastResponseData.set(resp.data ?? []);
        this.showRightPanel.set(true);

        // Defer session loading so chart rendering completes first.
        setTimeout(() => this.loadSessions(), 0);
      });
    } catch (err) {
      // AbortError means the user clicked stop or sent a new message – not an error.
      if (err instanceof Error && err.name === 'AbortError') {
        this._resetStreamingState();
        return;
      }

      this.messages.update((list) => {
        if (assistantIdx < 0 || assistantIdx >= list.length) return list;
        const copy    = [...list];
        const current = copy[assistantIdx];
        copy[assistantIdx] = {
          ...current,
          content: 'Something went wrong. Please try again.',
          timestamp: new Date(),
          error: true,
          responseType: 'text_only',
          isStreaming: false,
        };
        return copy;
      });
    } finally {
      this._resetStreamingState();
      this.scrollToBottom();
    }
  }

  // ── _resetStreamingState – shared cleanup helper ───────────────────────────
  // Resets all streaming/typing signals to idle state.
  // Called by stopGeneration(), error handlers, and completion handlers.
  private _resetStreamingState(): void {
    this.isTyping.set(false);
    this.isStreaming.set(false);
    this.streamingText.set('');
    this.streamingMessageIdx.set(-1);
    this.processingStages.set([]);
    this.processingProgress.set(0);
    this.processingSummary.set('Preparing LC request...');
    this.liveStatusLabel.set('Reviewing LC Context...');
  }

  private mapStageToStatusLabel(stage: ProcessingStageUpdate): string {
    if (stage.status === 'failed') return 'Failed';

    switch (stage.stageKey) {
      case 'query_reception':
      case 'query_parse':
      case 'context_retrieval':
        return 'Reviewing LC Context...';
      case 'tool_invocation':
      case 'query_building':
      case 'execution_plan':
      case 'db_execution':
        return 'Running Trade Intelligence...';
      case 'result_processing':
        return 'Structuring LC Insights...';
      case 'response_generation':
      case 'final_output':
      case 'completed':
        return 'Drafting LC Response...';
      default:
        return 'Reviewing LC Context...';
    }
  }

  private scrollToBottom(): void {
    setTimeout(() => {
      const el = document.getElementById('chat-bottom');
      el?.scrollIntoView({ behavior: 'smooth' });
    }, 50);
  }
}
