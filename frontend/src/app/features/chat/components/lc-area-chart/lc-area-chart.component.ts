import {
  Component, Input, OnDestroy, AfterViewInit, OnChanges, SimpleChanges,
  ElementRef, ViewChild, ChangeDetectionStrategy
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

@Component({
  selector: 'app-lc-area-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="area-chart-wrap">
      <div class="chart-header">
        <span class="chart-title">Cumulative Trend</span>
        <span class="chart-badge">Area</span>
      </div>
      <div style="height:260px;position:relative;">
        <canvas #chartCanvas></canvas>
      </div>
    </div>
  `,
  styles: [`
    .area-chart-wrap { padding:4px 0; }
    .chart-header { display:flex; align-items:center; gap:8px; margin-bottom:10px; }
    .chart-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .chart-badge { font-size:10px; padding:2px 8px; border-radius:99px;
      background:rgba(24,95,165,0.12); color:#185FA5; font-weight:600; }
  `]
})
export class LcAreaChartComponent implements AfterViewInit, OnDestroy, OnChanges {
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
    const labels = this.data.map(d => d['MonthLabel'] ?? d['Date'] ?? '');
    const values  = this.data.map(d => d['TotalValue'] ?? d['LcCount'] ?? 0);

    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'line',
      data: {
        labels,
        datasets: [{
          label: 'Total Value',
          data: values,
          borderColor: '#185FA5',
          backgroundColor: 'rgba(24,95,165,0.12)',
          borderWidth: 2,
          fill: true,
          tension: 0.35,
          pointRadius: 4,
          pointHoverRadius: 6,
        }]
      },
      options: {
        responsive: true, maintainAspectRatio: false,
        interaction: { mode:'index', intersect:false },
        plugins: {
          legend: { position:'bottom', labels:{ font:{size:11}, boxWidth:12 } },
          tooltip: {
            callbacks: {
              label: (ctx) => {
                const v = ctx.parsed.y ?? 0;
                return ` ${ctx.dataset.label}: ${v >= 1e6 ? 'EUR '+(v/1e6).toFixed(2)+'m' : v.toLocaleString()}`;
              }
            }
          }
        },
        scales: {
          y: {
            ticks: {
              color:'#888', font:{size:11},
              callback: (v: any) => { if (v == null) return v; const n = Number(v); return n >= 1e6 ? `${(n/1e6).toFixed(1)}m` : n >= 1e3 ? `${(n/1e3).toFixed(0)}k` : n; }
            },
            grid: { color:'rgba(0,0,0,0.06)' }
          },
          x: { ticks:{color:'#888', font:{size:11}}, grid:{display:false} }
        }
      }
    });
  }
}
