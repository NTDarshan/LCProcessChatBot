import {
  Component, Input, OnDestroy, OnChanges, SimpleChanges,
  ElementRef, ViewChild, AfterViewInit, ChangeDetectionStrategy
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

@Component({
  selector: 'app-lc-line-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="line-chart-wrap">
      <div class="chart-header">
        <span class="chart-title">{{ title }}</span>
        <span class="chart-badge">Trend</span>
      </div>
      <div class="canvas-box" style="height:260px;position:relative;">
        <canvas #chartCanvas></canvas>
      </div>
    </div>
  `,
  styles: [`
    .line-chart-wrap { padding: 4px 0; }
    .chart-header { display:flex; align-items:center; gap:8px; margin-bottom:10px; }
    .chart-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .chart-badge { font-size:10px; padding:2px 8px; border-radius:99px;
      background:rgba(24,95,165,0.12); color:#185FA5; font-weight:600; }
    .canvas-box canvas { width:100% !important; }
  `]
})
export class LcLineChartComponent implements AfterViewInit, OnDestroy, OnChanges {
  @Input() data: any[] = [];
  @Input() intent?: string;
  @Input() queryType?: string;

  @ViewChild('chartCanvas') canvasRef!: ElementRef<HTMLCanvasElement>;
  private chart?: Chart;

  get title(): string {
    if (this.data.length > 0 && this.data[0]['Year']) {
      return `Monthly Trend — ${this.data[0]['Year']}`;
    }
    return 'Monthly Trend';
  }

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
    const labels = this.data.map(d => d['MonthLabel'] ?? d['Date'] ?? d['Period'] ?? '');
    const counts  = this.data.map(d => d['LcCount']   ?? d['Count'] ?? 0);
    const values  = this.data.map(d => d['TotalValue'] ?? d['TotalLcValue'] ?? null);
    const hasValues = values.some(v => v !== null && v !== 0);

    const datasets: any[] = [{
      label: 'LC Count',
      data: counts,
      borderColor: '#185FA5',
      backgroundColor: 'rgba(24,95,165,0.08)',
      borderWidth: 2,
      tension: 0.35,
      pointRadius: 4,
      pointHoverRadius: 6,
      yAxisID: 'y',
    }];

    if (hasValues) {
      datasets.push({
        label: 'Total Value',
        data: values,
        borderColor: '#EF9F27',
        backgroundColor: 'rgba(239,159,39,0.06)',
        borderWidth: 2,
        tension: 0.35,
        pointRadius: 4,
        pointHoverRadius: 6,
        yAxisID: 'y1',
      });
    }

    const scales: any = {
      y: {
        type: 'linear', position: 'left',
        ticks: { color:'#888', font:{size:11}, precision:0 },
        grid: { color:'rgba(0,0,0,0.06)' },
        title: { display:true, text:'LC Count', color:'#185FA5', font:{size:11} }
      },
      x: { ticks:{color:'#888', font:{size:11}}, grid:{display:false} }
    };

    if (hasValues) {
      scales['y1'] = {
        type: 'linear', position: 'right',
        grid: { drawOnChartArea: false },
        ticks: {
          color: '#EF9F27', font:{size:11},
          callback: (v: any) => { if (v == null) return v; const n = Number(v); return n >= 1e6 ? `${(n/1e6).toFixed(1)}m` : n >= 1e3 ? `${(n/1e3).toFixed(0)}k` : n; }
        },
        title: { display:true, text:'Value (EUR)', color:'#EF9F27', font:{size:11} }
      };
    }

    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'line',
      data: { labels, datasets },
      options: {
        responsive: true, maintainAspectRatio: false,
        interaction: { mode:'index', intersect:false },
        plugins: {
          legend: { position:'bottom', labels:{ font:{size:11}, boxWidth:12 } },
          tooltip: {
            callbacks: {
              label: (ctx) => {
                if (ctx.datasetIndex === 1) {
                  const v = ctx.parsed.y ?? 0;
                  return ` ${ctx.dataset.label}: EUR ${v >= 1e6 ? (v/1e6).toFixed(2)+'m' : v.toLocaleString()}`;
                }
                return ` ${ctx.dataset.label}: ${ctx.parsed.y}`;
              }
            }
          }
        },
        scales,
      }
    });
  }
}
