import {
  Component, Input, OnInit, OnDestroy, AfterViewInit,
  ElementRef, ViewChild, ChangeDetectionStrategy,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

interface BankRow { bank: string; lcCount: number; total: number; issued: number; paid: number; pending: number; }
type SortKey = 'bank' | 'lcCount' | 'total' | 'issued' | 'paid' | 'pending';

@Component({
  selector: 'app-lc-bank-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    @if (rows.length > 0) {
      <div class="bank-chart-wrap">

        <!-- Summary cards -->
        <div class="summary-row">
          <div class="s-card">
            <div class="s-val">{{ rows.length }}</div>
            <div class="s-lbl">Banks</div>
          </div>
          <div class="s-card">
            <div class="s-val">{{ fmtAmt(totalValue) }}</div>
            <div class="s-lbl">Total LC Value</div>
          </div>
          <div class="s-card">
            <div class="s-val">{{ totalLcs }}</div>
            <div class="s-lbl">Total LCs</div>
          </div>
        </div>

        <!-- Chart -->
        <div class="chart-area" [style.height.px]="chartHeight">
          <canvas #chartCanvas></canvas>
        </div>

        <!-- Sortable table -->
        <div class="table-scroll">
          <table class="bank-table">
            <thead>
              <tr>
                @for (col of tableCols; track col.key) {
                  <th (click)="sortBy(col.key)"
                      [class.active-sort]="sortKey === col.key"
                      [class.num-header]="col.key !== 'bank'"
                      class="sortable">
                    {{ col.label }}
                    <span class="sort-icon">{{ sortKey === col.key ? (sortAsc ? '↑' : '↓') : '↕' }}</span>
                  </th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of sortedRows; track row.bank; let i = $index) {
                <tr [class.top-row]="i === 0">
                  <td class="bank-cell">{{ row.bank }}</td>
                  <td class="num-cell">{{ row.lcCount }}</td>
                  <td class="num-cell amt-cell">{{ fmtAmtFull(row.total) }}</td>
                  <td class="num-cell">{{ row.issued }}</td>
                  <td class="num-cell">{{ row.paid }}</td>
                  <td class="num-cell">{{ row.pending }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

      </div>
    }
  `,
  styles: [`
    .bank-chart-wrap { display: flex; flex-direction: column; gap: 10px; margin-top: 8px; }

    .summary-row { display: flex; gap: 8px; }
    .s-card {
      flex: 1; background: #f8fafc; border: 1px solid #e5e7eb;
      border-radius: 10px; padding: 10px 14px; text-align: center;
    }
    .s-val { font-size: 18px; font-weight: 700; color: #111827; font-variant-numeric: tabular-nums; }
    .s-lbl { font-size: 11px; color: #9ca3af; margin-top: 2px; }

    .chart-area { position: relative; width: 100%; min-height: 160px; }
    .chart-area canvas { width: 100% !important; height: 100% !important; }

    .table-scroll { overflow-x: auto; border-radius: 10px; border: 1px solid #e5e7eb; }
    .bank-table { width: 100%; border-collapse: collapse; font-size: 13px; }
    .bank-table thead tr { background: #f8fafc; }
    .bank-table th {
      padding: 8px 12px; text-align: left; font-weight: 600; font-size: 11px;
      color: #6b7280; white-space: nowrap; border-bottom: 1px solid #e5e7eb;
      text-transform: uppercase; letter-spacing: 0.04em;
      cursor: default; vertical-align: middle;
    }
    .bank-table th.num-header { text-align: right; }
    .bank-table th.sortable { cursor: pointer; user-select: none; }
    .bank-table th.sortable:hover { color: #374151; }
    .bank-table th.active-sort { color: #185FA5; }
    .sort-icon { margin-left: 4px; font-size: 10px; opacity: 0.6; }
    .bank-table td { padding: 8px 12px; border-bottom: 1px solid #f3f4f6; vertical-align: middle; }
    .bank-table tbody tr:last-child td { border-bottom: none; }
    .bank-table tbody tr:hover { background: #f9fafb; }
    .top-row td { font-weight: 600; }
    .bank-cell { color: #111827; font-weight: 500; }
    .num-cell { text-align: right; font-variant-numeric: tabular-nums; color: #374151; }
    .amt-cell { font-weight: 600; color: #111827; }
  `]
})
export class LcBankChartComponent implements OnInit, AfterViewInit, OnDestroy {
  @Input() data: Record<string, unknown>[] = [];
  @ViewChild('chartCanvas') canvasRef!: ElementRef<HTMLCanvasElement>;

  rows: BankRow[] = [];
  chart: Chart | null = null;

  sortKey: SortKey = 'total';
  sortAsc = false;

  readonly tableCols: { key: SortKey; label: string }[] = [
    { key: 'bank',    label: 'Bank'        },
    { key: 'lcCount', label: 'LCs'         },
    { key: 'total',   label: 'Total Value' },
    { key: 'issued',  label: 'Issued'      },
    { key: 'paid',    label: 'Paid'        },
    { key: 'pending', label: 'Pending'     },
  ];

  get totalValue(): number { return this.rows.reduce((s, r) => s + r.total, 0); }
  get totalLcs(): number   { return this.rows.reduce((s, r) => s + r.lcCount, 0); }
  get chartHeight(): number { return Math.max(200, this.rows.length * 36); }

  get sortedRows(): BankRow[] {
    return [...this.rows].sort((a, b) => {
      const av = a[this.sortKey], bv = b[this.sortKey];
      const cmp = typeof av === 'string' ? av.localeCompare(String(bv)) : (av as number) - (bv as number);
      return this.sortAsc ? cmp : -cmp;
    });
  }

  ngOnInit() {
    this.rows = this.data
      .map(d => ({
        bank:    String(d['Bank'] ?? '—'),
        lcCount: Number(d['LcCount'] ?? d['Count'] ?? 0),
        total:   parseFloat(String(d['TotalLcValue'] ?? d['TotalValue'] ?? 0)) || 0,
        issued:  Number(d['IssuedCount'] ?? 0),
        paid:    Number(d['PaidCount'] ?? 0),
        pending: Number(d['PendingCount'] ?? 0),
      }))
      .sort((a, b) => b.total - a.total);
  }

  ngAfterViewInit() { this.buildChart(); }

  ngOnDestroy() { this.chart?.destroy(); }

  sortBy(key: SortKey) {
    if (this.sortKey === key) { this.sortAsc = !this.sortAsc; }
    else { this.sortKey = key; this.sortAsc = key === 'bank'; }
  }

  private buildChart() {
    if (!this.canvasRef || !this.rows.length) return;
    const isDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    const textColor = isDark ? '#c2c0b6' : '#3d3d3a';
    const gridColor = isDark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.08)';

    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'bar',
      data: {
        labels: this.rows.map(r => r.bank),
        datasets: [{
          data: this.rows.map(r => r.total),
          backgroundColor: '#185FA5',
          borderRadius: 4,
          barThickness: 22,
        }],
      },
      options: {
        indexAxis: 'y',
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        plugins: {
          legend: { display: false },
          tooltip: {
            callbacks: {
              label: ctx => {
                const row = this.rows[ctx.dataIndex];
                return [
                  ` ${row.lcCount} LCs`,
                  ` Total: ${this.fmtAmtFull(row.total)}`,
                ];
              },
            },
          },
        },
        scales: {
          x: {
            grid: { color: gridColor },
            ticks: {
              color: textColor,
              callback: (v) => {
                if (v == null) return v;
                const n = Number(v);
                if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
                if (n >= 1_000)     return `${(n / 1_000).toFixed(0)}K`;
                return String(n);
              },
            },
            title: { display: true, text: 'Total LC Value', color: textColor, font: { size: 11 } },
          },
          y: {
            grid: { display: false },
            ticks: { color: textColor, font: { size: 12 } },
          },
        },
      },
    });
  }

  fmtAmt(n: number): string {
    if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
    if (n >= 1_000)     return `${(n / 1_000).toFixed(0)}K`;
    return String(Math.round(n));
  }

  fmtAmtFull(n: number): string {
    return new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 }).format(Math.round(n));
  }
}
