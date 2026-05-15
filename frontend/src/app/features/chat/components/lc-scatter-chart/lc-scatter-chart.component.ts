import {
  Component, Input, OnDestroy, AfterViewInit, OnChanges, SimpleChanges,
  ElementRef, ViewChild, ChangeDetectionStrategy
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

const BANK_COLORS: Record<string, string> = {
  BNP: '#185FA5', KBC: '#EF9F27', CACIB: '#639922',
  COMMERZBANK: '#E24B4A', OTHER: '#712B13'
};
const DEFAULT_COLORS = ['#185FA5','#EF9F27','#639922','#E24B4A','#712B13','#8B6BA0','#2A8C7A'];

function bankColor(bank: string, idx: number): string {
  const upper = (bank ?? '').toUpperCase();
  for (const key of Object.keys(BANK_COLORS)) {
    if (upper.includes(key)) return BANK_COLORS[key];
  }
  return DEFAULT_COLORS[idx % DEFAULT_COLORS.length];
}

@Component({
  selector: 'app-lc-scatter-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="scatter-wrap">
      <div class="chart-header">
        <span class="chart-title">LC Amount vs Processing Days</span>
        <span class="chart-badge">Scatter</span>
      </div>
      <div style="height:300px;position:relative;">
        <canvas #chartCanvas></canvas>
      </div>
      <p class="quadrant-note">Dashed lines mark median X and Y — four quadrants: high/low value × fast/slow</p>
    </div>
  `,
  styles: [`
    .scatter-wrap { padding:4px 0; }
    .chart-header { display:flex; align-items:center; gap:8px; margin-bottom:10px; }
    .chart-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .chart-badge { font-size:10px; padding:2px 8px; border-radius:99px;
      background:rgba(24,95,165,0.12); color:#185FA5; font-weight:600; }
    .quadrant-note { font-size:10px; color:#999; margin-top:6px; }
  `]
})
export class LcScatterChartComponent implements AfterViewInit, OnDestroy, OnChanges {
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
    // Group by bank
    const bankMap = new Map<string, any[]>();
    this.data.forEach(d => {
      const bank = d['Bank'] ?? 'Unknown';
      if (!bankMap.has(bank)) bankMap.set(bank, []);
      bankMap.get(bank)!.push({ x: d['LcAmount'] ?? 0, y: d['DaysPending'] ?? 0, raw: d });
    });

    const allX = this.data.map(d => d['LcAmount'] ?? 0);
    const allY = this.data.map(d => d['DaysPending'] ?? 0);
    const medX = this.median(allX);
    const medY = this.median(allY);

    let colorIdx = 0;
    const datasets: any[] = [];
    bankMap.forEach((pts, bank) => {
      const color = bankColor(bank, colorIdx++);
      datasets.push({
        label: bank,
        data: pts,
        backgroundColor: color + 'CC',
        borderColor: color,
        borderWidth: 1,
        pointRadius: 5,
        pointHoverRadius: 7,
      });
    });

    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'scatter',
      data: { datasets },
      options: {
        responsive: true, maintainAspectRatio: false,
        plugins: {
          legend: { position:'bottom', labels:{ font:{size:11}, boxWidth:12 } },
          tooltip: {
            callbacks: {
              label: (ctx) => {
                const pt = (ctx.raw as any);
                const amt = pt.x >= 1e6 ? `EUR ${(pt.x/1e6).toFixed(2)}m` : `EUR ${pt.x.toLocaleString()}`;
                return ` ${ctx.dataset.label} | ${amt} | ${pt.y} days`;
              }
            }
          }
        },
        scales: {
          x: {
            title:{ display:true, text:'LC Amount (EUR)', color:'#888', font:{size:11} },
            ticks:{
              color:'#888', font:{size:11},
              callback: (v: any) => { if (v == null) return v; const n = Number(v); return n >= 1e6 ? `${(n/1e6).toFixed(1)}m` : n >= 1e3 ? `${(n/1e3).toFixed(0)}k` : n; }
            },
            grid:{ color:'rgba(0,0,0,0.05)' }
          },
          y: {
            title:{ display:true, text:'Days Pending', color:'#888', font:{size:11} },
            ticks:{ color:'#888', font:{size:11}, precision:0 },
            grid:{ color:'rgba(0,0,0,0.05)' }
          }
        }
      },
      plugins: [{
        id: 'quadrantLines',
        afterDraw(chart: any) {
          const { ctx, chartArea, scales } = chart;
          const xPixel = scales.x.getPixelForValue(medX);
          const yPixel = scales.y.getPixelForValue(medY);
          ctx.save();
          ctx.setLineDash([5,4]);
          ctx.strokeStyle = 'rgba(0,0,0,0.18)';
          ctx.lineWidth = 1;
          ctx.beginPath(); ctx.moveTo(xPixel, chartArea.top); ctx.lineTo(xPixel, chartArea.bottom); ctx.stroke();
          ctx.beginPath(); ctx.moveTo(chartArea.left, yPixel); ctx.lineTo(chartArea.right, yPixel); ctx.stroke();
          ctx.restore();
        }
      }]
    });
  }

  private median(arr: number[]): number {
    if (!arr.length) return 0;
    const sorted = [...arr].sort((a,b) => a-b);
    const mid = Math.floor(sorted.length / 2);
    return sorted.length % 2 === 0 ? (sorted[mid-1]+sorted[mid])/2 : sorted[mid];
  }
}
