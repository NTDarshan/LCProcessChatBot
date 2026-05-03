import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ChatRequest, ChatResponse, ChatSession, ChatHistoryItem } from '../../shared/models/chat.models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // Base URL from environment; points to the .NET 10 Web API
  private readonly baseUrl = environment.apiUrl;

  // POST /api/chat/query – sends the user message and returns the AI response
  async sendMessage(req: ChatRequest): Promise<ChatResponse> {
    return firstValueFrom(
      this.http.post<ChatResponse>(`${this.baseUrl}/chat/query`, req)
    );
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
