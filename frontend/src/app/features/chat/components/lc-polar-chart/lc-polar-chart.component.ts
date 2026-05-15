import {
  Component, Input, OnDestroy, AfterViewInit, OnChanges, SimpleChanges,
  ElementRef, ViewChild, ChangeDetectionStrategy
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

const STATUS_COLORS: Record<string, string> = {
  'Draft': '#B4B2A9',
  'In Validation': '#FAC775',
  'Submitted_For_Validation': '#FAC775',
  'Pending N+1': '#EF9F27',
  'Submitted_N+1': '#EF9F27',
  'Pending N+2': '#BA7517',
  'Submitted_N+2': '#BA7517',
  'Issued': '#378ADD',
  'LCIssued': '#378ADD',
  'Paid': '#639922',
  'PaymentDone': '#639922',
  'Unpaid': '#E24B4A',
  'PaymentNotDone': '#E24B4A',
  'Rejected': '#F09595',
  'Cancelled': '#888780',
};

function getStatusColor(status: string): string {
  return STATUS_COLORS[status] ?? '#999';
}

@Component({
  selector: 'app-lc-polar-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="polar-wrap">
      <div class="chart-header">
        <span class="chart-title">Status Distribution</span>
        <span class="chart-badge">Polar Area</span>
      </div>
      <div style="height:320px;position:relative;max-width:380px;margin:0 auto;">
        <canvas #chartCanvas></canvas>
      </div>
    </div>
  `,
  styles: [`
    .polar-wrap { padding:4px 0; }
    .chart-header { display:flex; align-items:center; gap:8px; margin-bottom:10px; }
    .chart-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .chart-badge { font-size:10px; padding:2px 8px; border-radius:99px;
      background:rgba(24,95,165,0.12); color:#185FA5; font-weight:600; }
  `]
})
export class LcPolarChartComponent implements AfterViewInit, OnDestroy, OnChanges {
  @Input() data: any[] = [];
  @ViewChild('chartCanvas') canvasRef!: ElementRef<HTMLCanvasElement>;
  private chart?: Chart;

  ngAfterViewInit(): void { this.buildChart(); }
  ngOnDestroy(): void { this.chart?.destroy(); }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['data'] && !changes['data'].firstChange) {
      this.chart?.destroy();
      this.chart = undefined;
      this.buildChart();
    }
  }

  private buildChart(): void {
    const labels = this.data.map(d => d['HumanLabel'] ?? d['Status'] ?? '');
    const counts  = this.data.map(d => d['Count'] ?? d['LcCount'] ?? 0);
    const colors  = this.data.map(d => getStatusColor(d['HumanLabel'] ?? d['Status'] ?? ''));

    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'polarArea',
      data: {
        labels,
        datasets: [{
          data: counts,
          backgroundColor: colors.map(c => c + 'CC'),
          borderColor: colors,
          borderWidth: 1.5,
        }]
      },
      options: {
        responsive: true, maintainAspectRatio: false,
        plugins: {
          legend: { position:'bottom', labels:{ font:{size:11}, boxWidth:12 } },
          tooltip: {
            callbacks: {
              label: (ctx) => ` ${ctx.label}: ${ctx.parsed.r} LC(s)`
            }
          }
        },
        scales: {
          r: {
            ticks: { display:false },
            grid: { color:'rgba(0,0,0,0.07)' }
          }
        }
      }
    });
  }
}
