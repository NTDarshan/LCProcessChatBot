import { Component, Input, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-lc-comparison',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (data.length >= 2) {
      <div class="comp-wrap">

        <!-- Side-by-side comparison cards (first 2 rows) -->
        <div class="comp-grid">
          @for (item of displayData; track $index; let i = $index) {
            <div class="comp-card" [class.comp-winner]="i === winnerIndex">
              <div class="comp-header">
                <span class="comp-name">{{ getLabel(item) }}</span>
                @if (i === winnerIndex) {
                  <span class="winner-badge">Highest value</span>
                }
              </div>
              <div class="comp-amount">{{ formatAmount(item['TotalLcValue'] ?? item['LcAmount']) }}</div>
              <div class="comp-sub">Total LC value</div>
              @if (item['LcCount'] !== undefined) {
                <div class="comp-stats">
                  <div class="comp-stat">
                    <span class="stat-val">{{ item['LcCount'] ?? 0 }}</span>
                    <span class="stat-lbl">Total LCs</span>
                  </div>
                  @if (item['IssuedCount'] !== undefined) {
                    <div class="comp-stat">
                      <span class="stat-val">{{ item['IssuedCount'] ?? 0 }}</span>
                      <span class="stat-lbl">Issued</span>
                    </div>
                  }
                  @if (item['PaidCount'] !== undefined) {
                    <div class="comp-stat">
                      <span class="stat-val">{{ item['PaidCount'] ?? 0 }}</span>
                      <span class="stat-lbl">Paid</span>
                    </div>
                  }
                  @if (item['PendingCount'] !== undefined) {
                    <div class="comp-stat">
                      <span class="stat-val">{{ item['PendingCount'] ?? 0 }}</span>
                      <span class="stat-lbl">Pending</span>
                    </div>
                  }
                </div>
              }
            </div>
          }
        </div>

        <!-- Difference row (only when exactly 2 items) -->
        @if (data.length === 2 && difference !== null) {
          <div class="diff-row">
            <span class="diff-label">Difference</span>
            <span class="diff-value">{{ formatAmount(difference) }}</span>
            <span class="diff-pct">{{ diffPercent }}% more</span>
          </div>
        }

        <!-- Additional rows as compact list if more than 2 -->
        @if (data.length > 2) {
          <div class="extra-rows">
            @for (item of data.slice(2); track $index) {
              <div class="extra-row">
                <span class="extra-name">{{ getLabel(item) }}</span>
                <span class="extra-val">{{ formatAmount(item['TotalLcValue'] ?? item['LcAmount']) }}</span>
                <span class="extra-count">{{ item['LcCount'] ?? '' }} LCs</span>
              </div>
            }
          </div>
        }

      </div>
    }
  `,
  styles: [`
    .comp-wrap { display: flex; flex-direction: column; gap: 10px; }
    .comp-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
    .comp-card { border: 0.5px solid var(--color-border-tertiary, #e5e7eb); border-radius: 10px; padding: 14px; display: flex; flex-direction: column; gap: 4px; }
    .comp-card.comp-winner { border: 1.5px solid #185FA5; }
    .comp-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 6px; }
    .comp-name { font-size: 15px; font-weight: 500; color: var(--color-text-primary, #111827); }
    .winner-badge { font-size: 10px; padding: 2px 8px; border-radius: 10px; background: #E6F1FB; color: #0C447C; }
    .comp-amount { font-size: 22px; font-weight: 500; color: var(--color-text-primary, #111827); font-variant-numeric: tabular-nums; }
    .comp-sub { font-size: 11px; color: var(--color-text-secondary, #6b7280); margin-bottom: 10px; }
    .comp-stats { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; border-top: 0.5px solid var(--color-border-tertiary, #e5e7eb); padding-top: 8px; }
    .comp-stat { display: flex; flex-direction: column; gap: 1px; }
    .stat-val { font-size: 16px; font-weight: 500; color: var(--color-text-primary, #111827); }
    .stat-lbl { font-size: 11px; color: var(--color-text-secondary, #6b7280); }
    .diff-row { display: flex; align-items: center; gap: 10px; padding: 8px 12px; background: var(--color-background-secondary, #f8fafc); border-radius: 8px; font-size: 13px; }
    .diff-label { color: var(--color-text-secondary, #6b7280); flex: 1; }
    .diff-value { font-weight: 500; color: var(--color-text-primary, #111827); font-variant-numeric: tabular-nums; }
    .diff-pct { font-size: 11px; color: #155724; background: #D4EDDA; padding: 2px 8px; border-radius: 10px; }
    .extra-rows { display: flex; flex-direction: column; gap: 4px; }
    .extra-row { display: flex; align-items: center; gap: 10px; padding: 6px 10px; border-radius: 6px; background: var(--color-background-secondary, #f8fafc); }
    .extra-name { flex: 1; font-size: 13px; color: var(--color-text-primary, #111827); }
    .extra-val { font-size: 13px; font-weight: 500; color: var(--color-text-primary, #111827); font-variant-numeric: tabular-nums; }
    .extra-count { font-size: 11px; color: var(--color-text-secondary, #6b7280); }
  `]
})
export class LcComparisonComponent implements OnInit {
  @Input() data: Record<string, unknown>[] = [];
  @Input() intent: string = '';

  displayData: Record<string, unknown>[] = [];
  winnerIndex = 0;
  difference: number | null = null;
  diffPercent = '0';

  ngOnInit() {
    this.displayData = this.data.slice(0, 2);
    const vals = this.displayData.map(d => parseFloat(String(d['TotalLcValue'] ?? d['LcAmount'] ?? 0)) || 0);
    this.winnerIndex = vals[0] >= vals[1] ? 0 : 1;
    if (this.data.length === 2 && vals[0] && vals[1]) {
      const hi = Math.max(...vals), lo = Math.min(...vals);
      this.difference  = hi - lo;
      this.diffPercent = lo > 0 ? ((hi - lo) / lo * 100).toFixed(1) : '0';
    }
  }

  getLabel(item: Record<string, unknown>): string {
    return String(item['Bank'] ?? item['CustomerName'] ?? item['Status'] ?? '—');
  }

  formatAmount(value: unknown): string {
    if (value == null) return '—';
    const n = parseFloat(String(value));
    if (isNaN(n)) return '—';
    if (n >= 1_000_000) return (n / 1_000_000).toFixed(2) + 'M';
    return new Intl.NumberFormat('en-US').format(Math.round(n));
  }
}
