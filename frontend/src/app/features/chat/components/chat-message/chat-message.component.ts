import { Component, input, output, signal, ChangeDetectionStrategy, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Message } from '../../../../shared/models/chat.models';
import { MessageStore } from '../../services/message.store';
import { LcTableComponent }               from '../lc-table/lc-table.component';
import { LcMetricCardsComponent }         from '../lc-metric-cards/lc-metric-cards.component';
import { LcBankChartComponent }           from '../lc-bank-chart/lc-bank-chart.component';
import { LcStatusChartComponent }         from '../lc-status-chart/lc-status-chart.component';
import { LcCustomerChartComponent }       from '../lc-customer-chart/lc-customer-chart.component';
import { LcEmptyStateComponent }          from '../lc-empty-state/lc-empty-state.component';
import { LcDetailCardComponent }          from '../lc-detail-card/lc-detail-card.component';
import { LcInvoiceBreakdownComponent }    from '../lc-invoice-breakdown/lc-invoice-breakdown.component';
import { LcComparisonComponent }          from '../lc-comparison/lc-comparison.component';
import { LcTimelineFilteredComponent }    from '../lc-timeline-filtered/lc-timeline-filtered.component';
// ── NEW VISUAL FORMATTERS ────────────────────────────────────────────────────
import { LcLineChartComponent }           from '../lc-line-chart/lc-line-chart.component';
import { LcAreaChartComponent }           from '../lc-area-chart/lc-area-chart.component';
import { LcRadarChartComponent }          from '../lc-radar-chart/lc-radar-chart.component';
import { LcScatterChartComponent }        from '../lc-scatter-chart/lc-scatter-chart.component';
import { LcBubbleChartComponent }         from '../lc-bubble-chart/lc-bubble-chart.component';
import { LcPolarChartComponent }          from '../lc-polar-chart/lc-polar-chart.component';
import { LcMixedChartComponent }          from '../lc-mixed-chart/lc-mixed-chart.component';
import { LcRiskScorecardComponent }       from '../lc-risk-scorecard/lc-risk-scorecard.component';
import { LcKpiStripComponent }            from '../lc-kpi-strip/lc-kpi-strip.component';
import { LcExpiryHeatmapComponent }       from '../lc-expiry-heatmap/lc-expiry-heatmap.component';

const EMPTY_PHRASES = ['no data found', 'no records found', 'no results'];

@Component({
  selector: 'app-chat-message',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CommonModule,
    LcTableComponent,
    LcMetricCardsComponent,
    LcBankChartComponent,
    LcStatusChartComponent,
    LcCustomerChartComponent,
    LcEmptyStateComponent,
    LcDetailCardComponent,
    LcInvoiceBreakdownComponent,
    LcComparisonComponent,
    LcTimelineFilteredComponent,
    // New formatters
    LcLineChartComponent,
    LcAreaChartComponent,
    LcRadarChartComponent,
    LcScatterChartComponent,
    LcBubbleChartComponent,
    LcPolarChartComponent,
    LcMixedChartComponent,
    LcRiskScorecardComponent,
    LcKpiStripComponent,
    LcExpiryHeatmapComponent,
  ],
  templateUrl: './chat-message.component.html',
  styleUrl:    './chat-message.component.scss',
})
export class ChatMessageComponent {
  readonly message      = input.required<Message>();
  readonly userInitials = input<string>('?');
  readonly onRetry      = output<void>();

  private store = inject(MessageStore);

  readonly queryVisible = signal(false);
  readonly copied       = signal(false);

  formatTime(date: Date): string {
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  toggleQuery(): void { this.queryVisible.update(v => !v); }

  sendFollowUp(query: string): void {
    const q = query.replace(' ↗', '');
    this.store.sendMessage(q);
  }

  async copyQuery(): Promise<void> {
    const q = this.message().executedQuery;
    if (!q) return;
    try {
      await navigator.clipboard.writeText(q);
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2000);
    } catch { /* ignore */ }
  }

  get rows(): Record<string, unknown>[] {
    return (this.message().data ?? []) as Record<string, unknown>[];
  }

  get isEmptyResult(): boolean {
    if (this.rows.length > 0) return false;
    const content = (this.message().content ?? '').toLowerCase().trim();
    return EMPTY_PHRASES.some(p => content.includes(p));
  }

  /** Routes to richer components based on response type and query classification. */
  get effectiveResponseType(): string {
    const base = this.message().responseType ?? 'table';
    const data = this.rows;
    const queryType = this.message().queryType;

    if (base === 'bank_chart' && data.length === 2)
      return 'comparison';

    if (base === 'table' && data.length === 1 && data[0]?.['TotalActiveLCs'] !== undefined)
      return 'kpi_strip';

    if (base === 'table' && data.length === 1 && queryType !== 'aggregate' && queryType !== 'single_stat')
      return 'detail_card';

    if (base === 'metric_cards' && queryType === 'trend')
      return 'line_chart';

    return base;
  }

  /** Follow-up chip suggestions keyed by effective response type. */
  get followUpChips(): string[] {
    return FOLLOW_UP_MAP[this.effectiveResponseType] ?? [];
  }

  get shouldHideStreamingPlaceholder(): boolean {
    const msg = this.message();
    return msg.role === 'assistant'
      && msg.responseType === 'streaming'
      && !(msg.content ?? '').trim()
      && !msg.error;
  }
}

const FOLLOW_UP_MAP: Record<string, string[]> = {
  'line_chart':     ['Show as area chart ↗', 'Show by bank ↗', 'Compare years ↗'],
  'radar_chart':    ['Show as bar chart ↗', 'Show BNP details ↗', 'Show KBC details ↗'],
  'scatter_chart':  ['Show high value LCs ↗', 'Show delayed LCs ↗'],
  'kpi_strip':      ['Show active LCs ↗', 'Show expiring LCs ↗', 'Show status breakdown ↗'],
  'risk_scorecard': ['Show critical LCs ↗', 'Show expired LCs ↗', 'Show overdue LCs ↗'],
  'expiry_heatmap': ['Show expiring this month ↗', 'Show expired LCs ↗'],
  'mixed_chart':    ['Show trend by bank ↗', 'Show this year vs last year ↗'],
  'table':          ['Show status breakdown ↗', 'Show by bank ↗', 'Filter by expiry ↗'],
  'bank_chart':     ['Compare top 2 banks ↗', 'Show issued LCs ↗', 'Show pending approvals ↗'],
  'metric_cards':   ['Show as chart ↗', 'Show full list ↗', 'Filter by bank ↗'],
  'approval_list':  ['Show all pending ↗', 'Show expired LCs ↗', 'Show by bank ↗'],
  'timeline':       ['Show all amendments ↗', 'Show invoice status ↗'],
  'detail_card':    ['Show all issued LCs ↗', 'Show LC history ↗', 'Show invoice for this LC ↗'],
  'comparison':     ['Add a third bank ↗', 'Show issued only ↗', 'Show by customer ↗'],
};
