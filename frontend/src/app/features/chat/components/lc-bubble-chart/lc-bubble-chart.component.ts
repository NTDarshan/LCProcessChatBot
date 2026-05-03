import {
  Component, Input, OnDestroy, AfterViewInit,
  ElementRef, ViewChild, ChangeDetectionStrategy
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

const DEFAULT_COLORS = ['#185FA5','#EF9F27','#639922','#E24B4A','#712B13','#8B6BA0','#2A8C7A'];

function bankColorIdx(bank: string, idx: number): string {
  const u = (bank ?? '').toUpperCase();
  if (u.includes('BNP')) return '#185FA5';
  if (u.includes('KBC')) return '#EF9F27';
  if (u.includes('CACIB')) return '#639922';
  if (u.includes('COMMERZBANK')) return '#E24B4A';
  return DEFAULT_COLORS[idx % DEFAULT_COLORS.length];
}

@Component({
  selector: 'app-lc-bubble-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="bubble-wrap">
      <div class="chart-header">
        <span class="chart-title">Bank Exposure Overview</span>
        <span class="chart-badge">Bubble</span>
      </div>
      <p class="note">Bubble size = amendment count (or LC count). X = LC Value. Y = Avg Days Pending.</p>
      <div style="height:300px;position:relative;">
        <canvas #chartCanvas></canvas>
      </div>
    </div>
  `,
  styles: [`
    .bubble-wrap { padding:4px 0; }
    .chart-header { display:flex; align-items:center; gap:8px; margin-bottom:4px; }
    .chart-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .chart-badge { font-size:10px; padding:2px 8px; border-radius:99px;
      background:rgba(24,95,165,0.12); color:#185FA5; font-weight:600; }
    .note { font-size:10px; color:#999; margin:0 0 8px; }
  `]
})
export class LcBubbleChartComponent implements AfterViewInit, OnDestroy {
  @Input() data: any[] = [];
  @ViewChild('chartCanvas') canvasRef!: ElementRef<HTMLCanvasElement>;
  private chart?: Chart;

  ngAfterViewInit(): void {
    const datasets = this.data.map((row, i) => {
      const x = row['TotalLcValue'] ?? row['LcAmount'] ?? 0;
      const y = row['AvgDaysPending'] ?? row['AvgDaysToIssuance'] ?? row['DaysPending'] ?? 0;
      const sizeVal = row['AmendmentCount'] ?? row['LcCount'] ?? 1;
      const r = Math.max(4, Math.min(20, Math.sqrt(sizeVal) * 3));
      const color = bankColorIdx(row['Bank'] ?? '', i);
      return {
        label: row['Bank'] ?? `Row ${i+1}`,
        data: [{ x, y, r }],
        backgroundColor: color + 'AA',
        borderColor: color,
        borderWidth: 1.5,
      };
    });

    this.chart = new Chart(this.canvasRef.nativeElement, {
      type: 'bubble',
      data: { datasets },
      options: {
        responsive: true, maintainAspectRatio: false,
        plugins: {
          legend: { position:'bottom', labels:{ font:{size:11}, boxWidth:12 } },
          tooltip: {
            callbacks: {
              label: (ctx) => {
                const d = ctx.dataset.data[0] as any;
                const amt = d.x >= 1e6 ? `EUR ${(d.x/1e6).toFixed(2)}m` : `EUR ${d.x.toLocaleString()}`;
                return ` ${ctx.dataset.label} | ${amt} | ${d.y} days | size: ${d.r.toFixed(1)}`;
              }
            }
          }
        },
        scales: {
          x: {
            title:{ display:true, text:'LC Value (EUR)', color:'#888', font:{size:11} },
            ticks:{ color:'#888', font:{size:11},
              callback: (v: any) => { if (v == null) return v; const n = Number(v); return n >= 1e6 ? `${(n/1e6).toFixed(1)}m` : n; } },
            grid:{ color:'rgba(0,0,0,0.05)' }
          },
          y: {
            title:{ display:true, text:'Avg Days Pending', color:'#888', font:{size:11} },
            ticks:{ color:'#888', font:{size:11} },
            grid:{ color:'rgba(0,0,0,0.05)' }
          }
        }
      }
    });
  }

  ngOnDestroy(): void { this.chart?.destroy(); }
}
