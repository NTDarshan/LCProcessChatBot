import {
  Component, Input, OnDestroy, AfterViewInit, OnChanges, SimpleChanges,
  ElementRef, ViewChild, ChangeDetectionStrategy
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

const PALETTE = ['#185FA5','#EF9F27','#639922','#E24B4A','#712B13'];
const PALETTE_ALPHA = ['rgba(24,95,165,0.15)','rgba(239,159,39,0.15)','rgba(99,153,34,0.15)','rgba(226,75,74,0.15)','rgba(113,43,19,0.15)'];

@Component({
  selector: 'app-lc-radar-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="radar-wrap">
      <div class="chart-header">
        <span class="chart-title">Performance Comparison</span>
        <span class="chart-badge">Radar</span>
      </div>
      <div style="height:320px;position:relative;max-width:480px;margin:0 auto;">
        <canvas #chartCanvas></canvas>
      </div>
    </div>
  `,
  styles: [`
    .radar-wrap { padding:4px 0; }
    .chart-header { display:flex; align-items:center; gap:8px; margin-bottom:10px; }
    .chart-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .chart-badge { font-size:10px; padding:2px 8px; border-radius:99px;
      background:rgba(24,95,165,0.12); color:#185FA5; font-weight:600; }
  `]
})
export class LcRadarChartComponent implements AfterViewInit, OnDestroy, OnChanges {
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
    const axes = ['LcCount','TotalLcValue','IssuedCount','PaidCount','PendingCount','RejectedCount'];
    const axisLabels = ['LC Count','Total Value','Issued','Paid','Pending','Rejected'];

    // Find max per column for normalisation
    const maxes = axes.map(ax => Math.max(...this.data.map(d => d[ax] ?? 0), 1));

    const datasets = this.data.slice(0, 5).map((row, i) => ({
      label: row['Bank'] ?? `Entity ${i+1}`,
      data: axes.map((ax, j) => {
        const v = row[ax] ?? 0;
        return Math.round((v / maxes[j]) * 100);
      }),
      borderColor: PALETTE[i % PALETTE.length],
      backgroundColor: PALETTE_ALPHA[i % PALETTE_ALPHA.length],
      borderWidth: 2,
      pointRadius: 4,
      pointHoverRadius: 6,
    }));

    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'radar',
      data: { labels: axisLabels, datasets },
      options: {
        responsive: true, maintainAspectRatio: false,
        plugins: {
          legend: { position:'bottom', labels:{ font:{size:11}, boxWidth:12 } },
          tooltip: {
            callbacks: {
              label: (ctx) => ` ${ctx.dataset.label}: ${ctx.parsed.r}% (normalised)`
            }
          }
        },
        scales: {
          r: {
            min: 0, max: 100,
            ticks: { display:false },
            pointLabels: { color:'#555', font:{size:11} },
            grid: { color:'rgba(0,0,0,0.08)' },
            angleLines: { color:'rgba(0,0,0,0.08)' }
          }
        }
      }
    });
  }
}
