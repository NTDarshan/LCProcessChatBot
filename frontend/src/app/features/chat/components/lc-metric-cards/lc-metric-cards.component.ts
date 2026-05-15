import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { LcStatusChartComponent }   from '../lc-status-chart/lc-status-chart.component';
import { LcCustomerChartComponent } from '../lc-customer-chart/lc-customer-chart.component';
import { LcLineChartComponent }     from '../lc-line-chart/lc-line-chart.component';

@Component({
  selector: 'app-lc-metric-cards',
  standalone: true,
  imports: [CommonModule, LcStatusChartComponent, LcCustomerChartComponent, LcLineChartComponent],
  template: `
    @switch (chartType) {
      @case ('doughnut') {
        <app-lc-status-chart [data]="data" />
      }
      @case ('stacked_bar') {
        <app-lc-customer-chart [data]="data" />
      }
      @case ('line') {
        <app-lc-line-chart [data]="data" />
      }
      @default {
        @if (data.length > 0) {
          <div class="metric-grid">
            @for (item of data; track $index) {
              <div class="metric-card">
                <div class="metric-label">{{ getLabel(item) }}</div>
                <div class="metric-value">{{ getValue(item) }}</div>
                @if (getSub(item)) {
                  <div class="metric-sub">{{ getSub(item) }}</div>
                }
              </div>
            }
          </div>
        }
      }
    }
  `,
  styles: [`
    .metric-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(130px, 1fr));
      gap: 8px;
      margin-top: 8px;
    }
    .metric-card {
      background: #f8fafc;
      border: 1px solid #e5e7eb;
      border-radius: 12px;
      padding: 14px 16px;
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .metric-label { font-size: 11px; color: #9ca3af; font-weight: 500; }
    .metric-value { font-size: 26px; font-weight: 700; color: #111827; line-height: 1.1; }
    .metric-sub   { font-size: 11px; color: #6b7280; margin-top: 2px; font-variant-numeric: tabular-nums; }
  `]
})
export class LcMetricCardsComponent {
  @Input() data: Record<string, unknown>[] = [];
  @Input() intent = '';
  @Input() chartType: string | null = null;

  getLabel(item: Record<string, unknown>): string {
    const LABEL_KEYS = ['HumanLabel', 'StatusLabel', 'Bank', 'CustomerName',
      'application_status', 'Status', 'MonthLabel', 'Date'];
    for (const k of LABEL_KEYS) {
      const v = item[k];
      if (v != null && v !== '') return String(v);
    }
    for (const k of Object.keys(item)) {
      const v = item[k];
      if (typeof v === 'string' && v !== '' && isNaN(Number(v))) return v;
    }
    const firstKey = Object.keys(item)[0] ?? '';
    return firstKey.replace(/([A-Z])/g, ' $1').trim() || '—';
  }

  getValue(item: Record<string, unknown>): string {
    const COUNT_KEYS = ['Count', 'LcCount', 'AvgDaysToIssuance', 'TotalIssuedLCs',
      'AmendmentCount', 'IssuedCount', 'PaidCount', 'PendingCount'];
    for (const k of COUNT_KEYS) {
      const v = item[k];
      if (v != null) {
        const n = parseFloat(String(v));
        if (isFinite(n)) return Number.isInteger(n) ? String(n) : n.toFixed(1);
      }
    }
    for (const k of Object.keys(item)) {
      const v = item[k];
      if (v != null) {
        const n = parseFloat(String(v));
        if (isFinite(n)) return Number.isInteger(n) ? String(n) : n.toFixed(1);
      }
    }
    return '—';
  }

  getSub(item: Record<string, unknown>): string {
    const AMT_KEYS = ['TotalAmount', 'TotalLcValue', 'TotalValue', 'LcAmount'];
    for (const k of AMT_KEYS) {
      const raw = item[k];
      if (raw != null) {
        const n = parseFloat(String(raw));
        if (!isNaN(n)) {
          let fmt: string;
          if (n >= 1_000_000_000)      fmt = `${(n / 1_000_000_000).toFixed(2)}B`;
          else if (n >= 1_000_000)     fmt = `${(n / 1_000_000).toFixed(2)}M`;
          else if (n >= 1_000)         fmt = `${(n / 1_000).toFixed(1)}K`;
          else fmt = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 }).format(n);
          const cur = String(item['Currency'] ?? item['currency'] ?? '');
          return cur ? `${cur} ${fmt}` : fmt;
        }
      }
    }
    const min = item['MinDays'], max = item['MaxDays'];
    if (min != null && max != null) return `${min}–${max} days`;
    if (min != null) return `min ${min} days`;
    if (max != null) return `max ${max} days`;
    return '';
  }
}
