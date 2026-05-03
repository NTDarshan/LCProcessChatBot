import {
  signal, computed, effect, inject, Injectable
} from '@angular/core';
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
export class MessageStore {
  private readonly api = inject(ApiService);
  private readonly signalR = inject(SignalRService);

  // ── Core state ──────────────────────────────────────────────────────────────
  readonly messages           = signal<Message[]>([]);
  readonly isTyping           = signal<boolean>(false);
  readonly isStreaming        = signal<boolean>(false);
  readonly streamingText      = signal<string>('');
  readonly streamingMessageIdx = signal<number>(-1);
  readonly processingStages   = signal<ProcessingStageUpdate[]>([]);
  readonly processingProgress = signal<number>(0);
  readonly processingSummary  = signal<string>('Preparing LC request...');
  readonly liveStatusLabel    = signal<string>('Reviewing LC Context...');
  readonly sessionId          = signal<string | null>(null);
  readonly sessions           = signal<ChatSession[]>([]);
  readonly showRightPanel     = signal<boolean>(false);
  readonly isLoadingSessions  = signal<boolean>(false);
  readonly isLoadingHistory   = signal<boolean>(false);
  // Raw DB rows from the most recent assistant response – drives the right panel
  readonly lastResponseData   = signal<object[]>([]);

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

    this.signalR.connectionState$.subscribe((state) => {
      if (state === 'Connected') this.connectionStatus.set('connected');
      else if (state === 'Reconnecting') this.connectionStatus.set('connecting');
      else this.connectionStatus.set('disconnected');
    });

    this.signalR.chatChunk$.subscribe((chunk) => {
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
        const copy = [...list];
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

    this.signalR.chatComplete$.subscribe((dto: ChatResponse) => {
      const idx = this.streamingMessageIdx();
      if (idx < 0) return;

      if (dto.sessionId) this.sessionId.set(dto.sessionId);

      this.messages.update((list) => {
        if (idx < 0 || idx >= list.length) return list;
        const copy = [...list];
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
        };
        return copy;
      });

      this.isStreaming.set(false);
      this.isTyping.set(false);
      this.streamingText.set('');
      this.streamingMessageIdx.set(-1);
      this.processingStages.set([]);
      this.processingProgress.set(0);
      this.processingSummary.set('Preparing LC request...');
      this.liveStatusLabel.set('Reviewing LC Context...');

      this.lastResponseData.set(dto.data ?? []);
      this.showRightPanel.set(true);
      this.loadSessions();
      this.scrollToBottom();
    });

    this.signalR.chatError$.subscribe((text) => {
      const idx = this.streamingMessageIdx();
      if (idx < 0) return;

      this.messages.update((list) => {
        if (idx < 0 || idx >= list.length) return list;
        const copy = [...list];
        const current = copy[idx];
        copy[idx] = {
          ...current,
          content: text,
          responseType: 'text_only',
          isStreaming: false,
        };
        return copy;
      });

      this.isStreaming.set(false);
      this.isTyping.set(false);
      this.streamingText.set('');
      this.streamingMessageIdx.set(-1);
      this.processingStages.set([]);
      this.processingProgress.set(0);
      this.processingSummary.set('Preparing LC request...');
      this.liveStatusLabel.set('Failed');
      this.scrollToBottom();
    });

    this.signalR.processingStage$.subscribe((stage: ProcessingStageUpdate) => {
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
    if (this.isStreaming()) return;

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
          await this.signalR.sendMessage(userEmail, text, this.sessionId() ?? undefined);
          return;
        } catch {
          await this.sendMessageHttp(text, userEmail, this.streamingMessageIdx());
          return;
        }
      }

      await this.sendMessageHttp(text, userEmail, this.streamingMessageIdx());
    } catch (err) {
      this.isTyping.set(false);
      this.isStreaming.set(false);
      this.streamingText.set('');
      this.streamingMessageIdx.set(-1);
      this.processingStages.set([]);
      this.processingProgress.set(0);
      this.processingSummary.set('Preparing LC request...');
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
    const msgs = this.messages();
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
    this.messages.set([]);
    this.isTyping.set(false);
    this.processingStages.set([]);
    this.processingProgress.set(0);
    this.processingSummary.set('Preparing LC request...');
    this.liveStatusLabel.set('Reviewing LC Context...');
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
      const resp = await this.api.sendMessage({
        message: text,
        userEmail,
        sessionId: this.sessionId() ?? undefined,
      });

      if (resp.sessionId) {
        this.sessionId.set(resp.sessionId);
      }

      this.messages.update((list) => {
        if (assistantIdx < 0 || assistantIdx >= list.length) return list;
        const copy = [...list];
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
        };
        return copy;
      });

      this.lastResponseData.set(resp.data ?? []);
      this.showRightPanel.set(true);
      this.loadSessions();
    } catch {
      this.messages.update((list) => {
        if (assistantIdx < 0 || assistantIdx >= list.length) return list;
        const copy = [...list];
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
      this.isTyping.set(false);
      this.isStreaming.set(false);
      this.streamingText.set('');
      this.streamingMessageIdx.set(-1);
      this.processingStages.set([]);
      this.processingProgress.set(0);
      this.processingSummary.set('Preparing LC request...');
      this.liveStatusLabel.set('Reviewing LC Context...');
      this.scrollToBottom();
    }
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
