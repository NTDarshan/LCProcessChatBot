import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { Subject } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ChatRequest, ChatResponse, ChatSession, ChatHistoryItem } from '../../shared/models/chat.models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // Base URL from environment; points to the .NET 10 Web API
  private readonly baseUrl = environment.apiUrl;

  // ── AbortController for HTTP streaming requests ────────────────────────────
  // A single AbortController tracks the active chat/query HTTP request.
  // Calling abort() causes the underlying fetch to be cancelled and signals
  // the backend's HttpContext.RequestAborted token to fire, stopping the
  // entire processing pipeline cooperatively.
  private _activeAbortController: AbortController | null = null;

  /**
   * Abort the currently active sendMessage HTTP request (if any).
   * Called by MessageStore.stopGeneration() and before every new sendMessage
   * to prevent concurrent in-flight requests and race conditions.
   */
  abortActiveRequest(): void {
    if (this._activeAbortController) {
      this._activeAbortController.abort();
      this._activeAbortController = null;
    }
  }

  // POST /api/chat/query – sends the user message and returns the AI response.
  // Creates a fresh AbortController for each call; cancels the previous one first
  // to prevent race conditions between consecutive messages.
  async sendMessage(req: ChatRequest): Promise<ChatResponse> {
    // Cancel any in-flight request before starting a new one
    this.abortActiveRequest();

    // Create a fresh controller for this request
    const controller = new AbortController();
    this._activeAbortController = controller;

    try {
      // Use the native fetch signal to hook into HttpContext.RequestAborted
      // on the backend — this is the mechanism that fires the CancellationToken.
      const abort$ = new Subject<void>();
      controller.signal.addEventListener('abort', () => abort$.next());

      return await firstValueFrom(
        this.http
          .post<ChatResponse>(`${this.baseUrl}/chat/query`, req)
          .pipe(takeUntil(abort$))
      );
    } finally {
      // Clear the active controller once the request completes (success or error)
      if (this._activeAbortController === controller) {
        this._activeAbortController = null;
      }
    }
  }

  // GET /api/chat/sessions?email=... – returns all sessions for the logged-in user
  async getSessions(email: string): Promise<ChatSession[]> {
    return firstValueFrom(
      this.http.get<ChatSession[]>(`${this.baseUrl}/chat/sessions`, {
        params: { email }
      })
    );
  }

  // GET /api/chat/sessions/{sessionId}/history?email=... – returns the message history for a session
  async getSessionHistory(sessionId: string, email: string): Promise<ChatHistoryItem[]> {
    return firstValueFrom(
      this.http.get<ChatHistoryItem[]>(`${this.baseUrl}/chat/sessions/${sessionId}/history`, {
        params: { email }
      })
    );
  }

  // DELETE /api/chat/sessions/{sessionId}?email=... – soft-deletes the session and its history
  async deleteSession(sessionId: string, email: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.baseUrl}/chat/sessions/${sessionId}`, {
        params: { email }
      })
    );
  }
}
