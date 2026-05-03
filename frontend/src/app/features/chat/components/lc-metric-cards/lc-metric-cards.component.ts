import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-lc-metric-cards',
  standalone: true,
  imports: [CommonModule],
  template: `
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
    return String(
      item['application_status'] ?? item['Status'] ??
      item['CustomerName'] ?? item['beneficiary'] ??
      item['Bank'] ?? item['bank'] ?? '—'
    );
  }

  getValue(item: Record<string, unknown>): string {
    const v = item['Count'] ?? item['LcCount'] ?? item['AmendmentCount'];
    return v != null ? String(v) : '—';
  }

  getSub(item: Record<string, unknown>): string {
    const raw = item['TotalAmount'] ?? item['TotalLcValue'] ?? item['TotalValue'] ?? item['lc_amount'];
    if (raw == null) return '';
    const n = parseFloat(String(raw));
    if (isNaN(n)) return '';
    const cur = String(item['Currency'] ?? item['currency'] ?? '');
    const fmt = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 }).format(n);
    return `${cur} ${fmt}`.trim();
  }
}
