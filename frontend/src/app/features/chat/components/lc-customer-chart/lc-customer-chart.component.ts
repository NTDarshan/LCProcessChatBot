import {
  Component, Input, OnInit, OnDestroy, AfterViewInit, OnChanges, SimpleChanges,
  ElementRef, ViewChild, ChangeDetectionStrategy,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

interface CustomerRow {
  customer: string; lcCount: number; total: number;
  issued: number; paid: number; pending: number;
}

@Component({
  selector: 'app-lc-customer-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    @if (rows.length > 0) {
      <div class="customer-chart-wrap">

        <!-- Summary cards -->
        <div class="summary-row">
          <div class="s-card">
            <div class="s-val">{{ rows.length }}</div>
            <div class="s-lbl">Customers</div>
          </div>
          <div class="s-card">
            <div class="s-val">{{ totalLcs }}</div>
            <div class="s-lbl">Total LCs</div>
          </div>
          <div class="s-card">
            <div class="s-val">{{ fmtAmt(totalValue) }}</div>
            <div class="s-lbl">Total Value</div>
          </div>
        </div>

        <!-- Chart -->
        <div class="chart-area" [style.height.px]="chartHeight">
          <canvas #chartCanvas></canvas>
        </div>

        <!-- Show all toggle -->
        @if (rows.length > 10) {
          <div class="show-toggle">
            <button (click)="showAll = !showAll; rebuildChart()">
              {{ showAll ? 'Show top 10 ↑' : 'Show all ' + rows.length + ' ↓' }}
            </button>
          </div>
        }

        <!-- Sortable table -->
        <div class="table-scroll">
          <table class="cust-table">
            <thead>
              <tr>
                @for (col of tableCols; track col.key) {
                  <th (click)="sortBy(col.key)" class="sortable" [class.active-sort]="sortKey === col.key">
                    {{ col.label }}
                    <span class="sort-icon">{{ sortKey === col.key ? (sortAsc ? '↑' : '↓') : '↕' }}</span>
                  </th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of sortedRows; track row.customer; let i = $index) {
                <tr [class.top-row]="i === 0">
                  <td>{{ row.customer }}</td>
                  <td class="num">{{ row.lcCount }}</td>
                  <td class="num amt">{{ fmtAmtFull(row.total) }}</td>
                  <td class="num">{{ row.issued }}</td>
                  <td class="num">{{ row.paid }}</td>
                  <td class="num">{{ row.pending }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

      </div>
    }
  `,
  styles: [`
    .customer-chart-wrap { display: flex; flex-direction: column; gap: 10px; margin-top: 8px; }

    .summary-row { display: flex; gap: 8px; }
    .s-card {
      flex: 1; background: #f8fafc; border: 1px solid #e5e7eb;
      border-radius: 10px; padding: 10px 14px; text-align: center;
    }
    .s-val { font-size: 18px; font-weight: 700; color: #111827; font-variant-numeric: tabular-nums; }
    .s-lbl { font-size: 11px; color: #9ca3af; margin-top: 2px; }

    .chart-area { position: relative; width: 100%; min-height: 160px; }
    .chart-area canvas { width: 100% !important; height: 100% !important; }

    .show-toggle { text-align: center; }
    .show-toggle button {
      background: none; border: 1px solid #e5e7eb; padding: 5px 16px;
      border-radius: 6px; font-size: 12px; cursor: pointer; color: #6b7280;
      transition: background 0.15s;
    }
    .show-toggle button:hover { background: #f9fafb; }

    .table-scroll { overflow-x: auto; border-radius: 10px; border: 1px solid #e5e7eb; }
    .cust-table { width: 100%; border-collapse: collapse; font-size: 13px; }
    .cust-table thead tr { background: #f8fafc; }
    .cust-table th {
      padding: 8px 12px; text-align: left; font-weight: 600; font-size: 11px;
      color: #6b7280; white-space: nowrap; border-bottom: 1px solid #e5e7eb;
      text-transform: uppercase; letter-spacing: 0.04em; cursor: pointer; user-select: none;
    }
    .cust-table th:hover { color: #374151; }
    .cust-table th.active-sort { color: #185FA5; }
    .sort-icon { margin-left: 4px; font-size: 10px; opacity: 0.6; }
    .cust-table td { padding: 8px 12px; border-bottom: 1px solid #f3f4f6; }
    .cust-table tbody tr:last-child td { border-bottom: none; }
    .cust-table tbody tr:hover { background: #f9fafb; }
    .top-row td { font-weight: 600; }
    .num { text-align: right; font-variant-numeric: tabular-nums; color: #374151; }
    .amt { font-weight: 600; color: #111827; }
  `]
})
export class LcCustomerChartComponent implements OnInit, AfterViewInit, OnDestroy, OnChanges {
  @Input() data: Record<string, unknown>[] = [];
  @ViewChild('chartCanvas') canvasRef!: ElementRef<HTMLCanvasElement>;

  rows: CustomerRow[] = [];
  chart: Chart | null = null;
  showAll = false;
  sortKey: keyof CustomerRow = 'lcCount';
  sortAsc = false;

  readonly tableCols: { key: keyof CustomerRow; label: string }[] = [
    { key: 'customer', label: 'Customer'    },
    { key: 'lcCount',  label: 'LCs'         },
    { key: 'total',    label: 'Total Value' },
    { key: 'issued',   label: 'Issued'      },
    { key: 'paid',     label: 'Paid'        },
    { key: 'pending',  label: 'Pending'     },
  ];

  get totalLcs():   number { return this.rows.reduce((s, r) => s + r.lcCount, 0); }
  get totalValue(): number { return this.rows.reduce((s, r) => s + r.total, 0); }

  get chartRows(): CustomerRow[] {
    return this.showAll ? this.rows : this.rows.slice(0, 10);
  }

  get chartHeight(): number {
    return Math.min(400, Math.max(160, this.chartRows.length * 32));
  }

  get sortedRows(): CustomerRow[] {
    return [...this.rows].sort((a, b) => {
      const av = a[this.sortKey], bv = b[this.sortKey];
      const cmp = typeof av === 'string' ? av.localeCompare(String(bv)) : (av as number) - (bv as number);
      return this.sortAsc ? cmp : -cmp;
    });
  }

  ngOnInit() { this.mapRows(); }

  ngAfterViewInit() { this.buildChart(); }

  ngOnDestroy() { this.chart?.destroy(); }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['data'] && !changes['data'].firstChange) {
      this.chart?.destroy();
      this.chart = null;
      this.mapRows();
      this.buildChart();
    }
  }

  private mapRows(): void {
    this.rows = this.data
      .map(d => ({
        customer: String(d['CustomerName'] ?? d['SapCustomerName'] ?? '—'),
        lcCount:  Number(d['LcCount'] ?? 0),
        total:    parseFloat(String(d['TotalLcValue'] ?? 0)) || 0,
        issued:   Number(d['IssuedCount'] ?? 0),
        paid:     Number(d['PaidCount'] ?? 0),
        pending:  Number(d['PendingCount'] ?? 0),
      }))
      .sort((a, b) => b.lcCount - a.lcCount);
  }

  sortBy(key: keyof CustomerRow) {
    if (this.sortKey === key) { this.sortAsc = !this.sortAsc; }
    else { this.sortKey = key; this.sortAsc = key === 'customer'; }
  }

  rebuildChart() {
    this.chart?.destroy();
    this.chart = null;
    setTimeout(() => this.buildChart(), 0);
  }

  private buildChart() {
    if (!this.canvasRef || !this.chartRows.length) return;
    const isDark    = window.matchMedia('(prefers-color-scheme: dark)').matches;
    const textColor = isDark ? '#c2c0b6' : '#3d3d3a';
    const gridColor = isDark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.08)';

    const cr = this.chartRows;
    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'bar',
      data: {
        labels: cr.map(r => r.customer),
        datasets: [
          { label: 'Issued',  data: cr.map(r => r.issued),  backgroundColor: '#378ADD', stack: 's' },
          { label: 'Paid',    data: cr.map(r => r.paid),    backgroundColor: '#639922', stack: 's' },
          { label: 'Pending', data: cr.map(r => r.pending), backgroundColor: '#EF9F27', stack: 's' },
        ],
      },
      options: {
        indexAxis: 'y',
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        plugins: {
          legend: {
            display: true,
            position: 'top',
            labels: { color: textColor, font: { size: 11 }, boxWidth: 12, padding: 12 },
          },
          tooltip: {
            callbacks: {
              title: ctx => cr[ctx[0].dataIndex].customer,
              footer: ctx => {
                const row = cr[ctx[0].dataIndex];
                return `Total: ${row.lcCount} LCs`;
              },
            },
          },
        },
        scales: {
          x: {
            stacked: true,
            grid: { color: gridColor },
            ticks: { color: textColor, font: { size: 11 } },
            title: { display: true, text: 'LC Count', color: textColor, font: { size: 11 } },
          },
          y: {
            stacked: true,
            grid: { display: false },
            ticks: { color: textColor, font: { size: 11 } },
          },
        },
      },
    });
  }

  fmtAmt(n: number): string {
    if (n >= 1_000_000_000) return `${(n / 1_000_000_000).toFixed(1)}B`;
    if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
    if (n >= 1_000)     return `${(n / 1_000).toFixed(0)}K`;
    return String(Math.round(n));
  }

  fmtAmtFull(n: number): string {
    return new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 }).format(Math.round(n));
  }
}
