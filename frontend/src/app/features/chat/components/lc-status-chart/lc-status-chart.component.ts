import {
  Component, Input, OnInit, OnDestroy, AfterViewInit, OnChanges, SimpleChanges,
  ElementRef, ViewChild, ChangeDetectionStrategy,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

interface StatusRow { key: string; label: string; count: number; pct: number; color: string; }

const STATUS_COLORS: Record<string, string> = {
  Draft:                    '#B4B2A9',
  Submitted_For_Validation: '#FAC775',
  'Submitted_N+1':          '#EF9F27',
  'Submitted_N+2':          '#BA7517',
  LCIssued:                 '#378ADD',
  PaymentDone:              '#639922',
  PaymentNotDone:           '#E24B4A',
  Rejected:                 '#F09595',
  Cancelled:                '#888780',
};

const STATUS_LABELS: Record<string, string> = {
  Draft:                    'Draft',
  Submitted_For_Validation: 'In Validation',
  'Submitted_N+1':          'Pending N+1',
  'Submitted_N+2':          'Pending N+2',
  LCIssued:                 'Issued',
  PaymentDone:              'Paid',
  PaymentNotDone:           'Unpaid',
  Rejected:                 'Rejected',
  Cancelled:                'Cancelled',
};

@Component({
  selector: 'app-lc-status-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    @if (statusRows.length > 0) {
      <div class="status-chart-wrap">

        <div class="chart-legend-row">
          <!-- Doughnut -->
          <div class="doughnut-area">
            <canvas #chartCanvas></canvas>
            <div class="center-label">
              <span class="center-count">{{ total }}</span>
              <span class="center-text">Total</span>
            </div>
          </div>

          <!-- Legend -->
          <div class="legend-list">
            @for (row of statusRows; track row.key) {
              <div class="legend-item">
                <span class="dot" [style.background]="row.color"></span>
                <span class="legend-label">{{ row.label }}</span>
                <span class="legend-count">{{ row.count }}</span>
                <span class="legend-pct">{{ row.pct.toFixed(1) }}%</span>
              </div>
            }
          </div>
        </div>

        <p class="summary-text">
          {{ total }} total LCs across {{ statusRows.length }} status{{ statusRows.length !== 1 ? 'es' : '' }}
        </p>

      </div>
    }
  `,
  styles: [`
    .status-chart-wrap { display: flex; flex-direction: column; gap: 12px; margin-top: 8px; }

    .chart-legend-row { display: flex; gap: 20px; align-items: center; flex-wrap: wrap; }

    .doughnut-area { position: relative; width: 180px; height: 180px; flex-shrink: 0; }
    .doughnut-area canvas { width: 180px !important; height: 180px !important; }
    .center-label {
      position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%);
      text-align: center; pointer-events: none;
    }
    .center-count { display: block; font-size: 24px; font-weight: 700; color: #111827; line-height: 1.1; }
    .center-text  { display: block; font-size: 11px; color: #9ca3af; }

    .legend-list { display: flex; flex-direction: column; gap: 6px; flex: 1; min-width: 180px; }
    .legend-item { display: flex; align-items: center; gap: 8px; }
    .dot { width: 10px; height: 10px; border-radius: 50%; flex-shrink: 0; }
    .legend-label { font-size: 12px; color: #374151; flex: 1; }
    .legend-count { font-size: 12px; font-weight: 600; color: #111827; font-variant-numeric: tabular-nums; min-width: 32px; text-align: right; }
    .legend-pct   { font-size: 11px; color: #9ca3af; min-width: 44px; text-align: right; }

    .summary-text { font-size: 12px; color: #6b7280; margin: 0; }
  `]
})
export class LcStatusChartComponent implements OnInit, AfterViewInit, OnDestroy, OnChanges {
  @Input() data: Record<string, unknown>[] = [];
  @ViewChild('chartCanvas') canvasRef!: ElementRef<HTMLCanvasElement>;

  statusRows: StatusRow[] = [];
  total = 0;
  chart: Chart | null = null;

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
    this.total = this.data.reduce((s, d) => s + (Number(d['Count'] ?? d['LcCount'] ?? 0)), 0);
    this.statusRows = this.data
      .map(d => {
        const key   = String(d['Status'] ?? d['application_status'] ?? '');
        const count = Number(d['Count'] ?? d['LcCount'] ?? 0);
        return {
          key,
          label: STATUS_LABELS[key] ?? key,
          count,
          pct:   this.total > 0 ? (count / this.total) * 100 : 0,
          color: STATUS_COLORS[key] ?? '#94a3b8',
        };
      })
      .sort((a, b) => b.count - a.count);
  }

  private buildChart() {
    if (!this.canvasRef || !this.statusRows.length) return;

    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'doughnut',
      data: {
        labels: this.statusRows.map(r => r.label),
        datasets: [{
          data:            this.statusRows.map(r => r.count),
          backgroundColor: this.statusRows.map(r => r.color),
          borderWidth: 2,
          borderColor: '#ffffff',
        }],
      },
      options: {
        cutout: '65%',
        animation: false,
        responsive: false,
        plugins: {
          legend: { display: false },
          tooltip: {
            callbacks: {
              label: ctx => {
                const row = this.statusRows[ctx.dataIndex];
                return ` ${row.label}: ${row.count} (${row.pct.toFixed(1)}%)`;
              },
            },
          },
        },
      },
    });
  }
}
