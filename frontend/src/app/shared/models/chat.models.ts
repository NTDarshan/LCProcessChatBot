export type MessageRole = 'user' | 'assistant';
export type ConnectionStatus = 'connected' | 'connecting' | 'disconnected';

export interface Message {
  id: string;
  role: MessageRole;
  content: string;
  timestamp: Date;
  isStreaming?: boolean;
  error?: boolean;
  metadata?: Record<string, unknown>;
  /** Parameterized SQL returned by the backend (assistant messages only). */
  executedQuery?: string;
  /** UI toggle – true = query panel is expanded. Never sent to the server. */
  showQuery?: boolean;
  /** Raw DB rows from the last query – drives the data table / cards. */
  data?: Record<string, unknown>[];
  /** Intent matched by the backend e.g. "PendingApprovals". */
  intent?: string;
  /** Layout hint from backend: "table" | "approval_list" | "metric_cards" | "bank_chart" | "timeline". */
  responseType?: string;
  /** Sub-chart hint: "doughnut" | "stacked_bar" | "count_grid" | "horizontal_bar" | null */
  chartType?: string | null;
  /** True when the backend used AI-generated Text-to-SQL instead of a hardcoded query. */
  isTextToSql?: boolean;
  /** AI query classification: "list"|"aggregate"|"single_stat"|"timeline"|"comparison"|"trend"|"correlation"|"risk"|"kpi"|"heatmap" */
  queryType?: string | null;
}

export interface ChatSession {
  sessionId: string;
  createdAt: Date;
  title: string | null;   // first user message; null if session has no messages yet
}

// Single turn returned by the history endpoint
export interface ChatHistoryItem {
  role: 'user' | 'assistant';
  content: string;
  executedQuery?: string;  // only present on assistant messages
  intent?: string;
  responseType?: string;
  queryType?: string | null;
  data?: string;           // JSON string from the database
  createdAt: Date;
}


// Matches the backend ChatRequestDto
export interface ChatRequest {
  message: string;
  userEmail: string;
  sessionId?: string | null;
}

// Matches the backend ChatResponseDto
export interface ChatResponse {
  response: string;
  data: object[];
  sessionId: string;
  /** Parameterized SQL that was executed. Null when ShowSqlQuery flag is off. */
  executedQuery?: string;
  /** Intent matched by the backend. */
  intent?: string;
  /** Layout hint from backend. */
  responseType?: string;
  /** Human-readable label for the response card header. */
  responseLabel?: string;
  /** Sub-chart hint: "doughnut" | "stacked_bar" | "count_grid" | "horizontal_bar" | null */
  chartType?: string | null;
  /** True when the backend used AI-generated Text-to-SQL. */
  isTextToSql?: boolean;
  /** AI query classification from backend. */
  queryType?: string | null;
}

export interface ProcessingStageUpdate {
  sequence: number;
  stageKey: string;
  stageName: string;
  liveLabel?: string | null;
  status: 'in_progress' | 'completed' | 'failed';
  progressPercent: number;
  estimatedMsRemaining?: number | null;
  elapsedMs?: number | null;
  errorMessage?: string | null;
  technicalDetails?: Record<string, string> | null;
}

export interface CachedResponse {
  response: string;
  cachedAt: number;
}

export interface LcDetail {
  lcNumber: string;
  status: 'Active' | 'Expired' | 'Pending';
  applicant: string;
  issuingBank: string;
  lcValue: string;
  issueDate: string;
  expiryDate: string;
}

export interface KeyInsight {
  label: string;
  value: string | number;
  trend: 'up' | 'down' | 'neutral';
  trendPercent: number;
  icon: string;
  color: string;
}

export interface SuggestedQuery {
  icon: string;
  text: string;
  description?: string;
}
