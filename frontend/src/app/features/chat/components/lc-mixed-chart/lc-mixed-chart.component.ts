import {
  Component, Input, OnDestroy, AfterViewInit, OnChanges, SimpleChanges,
  ElementRef, ViewChild, ChangeDetectionStrategy
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

@Component({
  selector: 'app-lc-mixed-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="mixed-wrap">
      <div class="chart-header">
        <span class="chart-title">Monthly Count + Cumulative</span>
        <span class="chart-badge">Mixed</span>
      </div>
      <div style="height:280px;position:relative;">
        <canvas #chartCanvas></canvas>
      </div>
    </div>
  `,
  styles: [`
    .mixed-wrap { padding:4px 0; }
    .chart-header { display:flex; align-items:center; gap:8px; margin-bottom:10px; }
    .chart-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .chart-badge { font-size:10px; padding:2px 8px; border-radius:99px;
      background:rgba(24,95,165,0.12); color:#185FA5; font-weight:600; }
  `]
})
export class LcMixedChartComponent implements AfterViewInit, OnDestroy, OnChanges {
  @Input() data: any[] = [];
  @ViewChild('chartCanvas') canvasRef!: ElementRef<HTMLCanvasElement>;
  private chart?: Chart;
  private cumulativeData: number[] = [];

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
    // Compute cumulative in component
    let running = 0;
    this.cumulativeData = this.data.map(d => {
      running += (d['LcCount'] ?? 0);
      return running;
    });

    const labels = this.data.map(d => d['MonthLabel'] ?? d['Date'] ?? '');
    const counts  = this.data.map(d => d['LcCount'] ?? 0);

    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'bar',
      data: {
        labels,
        datasets: [
          {
            type: 'bar' as any,
            label: 'Monthly Count',
            data: counts,
            backgroundColor: 'rgba(24,95,165,0.75)',
            borderColor: '#185FA5',
            borderWidth: 1,
            borderRadius: 4,
            yAxisID: 'y',
          },
          {
            type: 'line' as any,
            label: 'Cumulative',
            data: this.cumulativeData,
            borderColor: '#EF9F27',
            backgroundColor: 'transparent',
            borderWidth: 2,
            tension: 0.3,
            pointRadius: 4,
            pointHoverRadius: 6,
            fill: false,
            yAxisID: 'y1',
          }
        ]
      },
      options: {
        responsive: true, maintainAspectRatio: false,
        interaction: { mode:'index', intersect:false },
        plugins: {
          legend: { position:'bottom', labels:{ font:{size:11}, boxWidth:12 } },
        },
        scales: {
          y: {
            type:'linear', position:'left',
            ticks:{ color:'#888', font:{size:11}, precision:0 },
            grid:{ color:'rgba(0,0,0,0.06)' },
            title:{ display:true, text:'Count', color:'#185FA5', font:{size:11} }
          },
          y1: {
            type:'linear', position:'right',
            grid:{ drawOnChartArea:false },
            ticks:{ color:'#EF9F27', font:{size:11}, precision:0 },
            title:{ display:true, text:'Cumulative', color:'#EF9F27', font:{size:11} }
          },
          x: { ticks:{color:'#888', font:{size:11}}, grid:{display:false} }
        }
      }
    });
  }
}
