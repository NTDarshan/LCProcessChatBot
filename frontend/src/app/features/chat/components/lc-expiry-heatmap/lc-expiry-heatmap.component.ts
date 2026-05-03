import {
  Component, Input, OnInit, ChangeDetectionStrategy, inject
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { MessageStore } from '../../services/message.store';

type Urgency = 'expired' | 'critical' | 'warning' | 'normal' | 'none';

interface DayData {
  date: Date;
  count: number;
  urgency: Urgency;
  lcNumbers: string;
}

interface CalDay {
  day: number | null;
  data: DayData | null;
}

const URGENCY_COLORS: Record<Urgency, { bg: string; dot: string }> = {
  expired:  { bg:'#FCEBEB', dot:'#E24B4A' },
  critical: { bg:'#FAEEDA', dot:'#D85A30' },
  warning:  { bg:'#FFF3CD', dot:'#EF9F27' },
  normal:   { bg:'#E6F1FB', dot:'#185FA5' },
  none:     { bg:'transparent', dot:'transparent' },
};

@Component({
  selector: 'app-lc-expiry-heatmap',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="heatmap-wrap">
      <div class="heatmap-header">
        <span class="heatmap-title">LC Expiry Calendar</span>
        <span class="heatmap-badge">Heatmap</span>
      </div>

      @for (month of months; track month.label) {
        <div class="month-block">
          <div class="month-label">{{ month.label }}</div>
          <div class="day-headers">
            @for (h of dayHeaders; track h) { <span class="dh">{{ h }}</span> }
          </div>
          <div class="cal-grid">
            @for (cell of month.cells; track $index) {
              @if (cell.day === null) {
                <div class="cal-cell empty"></div>
              } @else if (cell.data) {
                <div class="cal-cell has-data"
                  [style.background]="urgencyStyle(cell.data.urgency).bg"
                  [title]="cell.data.count + ' LC(s): ' + cell.data.lcNumbers"
                  (click)="sendQuery('Show LCs expiring on ' + formatDate(cell.data.date))">
                  <span class="day-num">{{ cell.day }}</span>
                  <span class="dot" [style.background]="urgencyStyle(cell.data.urgency).dot"></span>
                </div>
              } @else {
                <div class="cal-cell">
                  <span class="day-num muted">{{ cell.day }}</span>
                </div>
              }
            }
          </div>
        </div>
      }

      <div class="legend">
        <span class="legend-item"><span class="leg-dot" style="background:#E24B4A"></span>Expired</span>
        <span class="legend-item"><span class="leg-dot" style="background:#D85A30"></span>&lt;7 days</span>
        <span class="legend-item"><span class="leg-dot" style="background:#EF9F27"></span>&lt;30 days</span>
        <span class="legend-item"><span class="leg-dot" style="background:#185FA5"></span>&gt;30 days</span>
      </div>
    </div>
  `,
  styles: [`
    .heatmap-wrap { padding:4px 0; }
    .heatmap-header { display:flex; align-items:center; gap:8px; margin-bottom:12px; }
    .heatmap-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .heatmap-badge { font-size:10px; padding:2px 8px; border-radius:99px;
      background:rgba(24,95,165,0.12); color:#185FA5; font-weight:600; }
    .month-block { margin-bottom:16px; }
    .month-label { font-size:12px; font-weight:600; color:#555; margin-bottom:6px; }
    .day-headers { display:grid; grid-template-columns:repeat(7,1fr); gap:2px; margin-bottom:3px; }
    .dh { font-size:9px; color:#aaa; text-align:center; font-weight:600; }
    .cal-grid { display:grid; grid-template-columns:repeat(7,1fr); gap:2px; }
    .cal-cell {
      height:32px; border-radius:5px; display:flex; flex-direction:column;
      align-items:center; justify-content:center; position:relative;
      background:var(--color-background-secondary,#f5f7fb);
      transition:opacity 0.1s;
    }
    .cal-cell.empty { background:transparent; }
    .cal-cell.has-data { cursor:pointer; }
    .cal-cell.has-data:hover { opacity:0.8; }
    .day-num { font-size:10px; color:#555; line-height:1; }
    .day-num.muted { color:#bbb; }
    .dot { width:5px; height:5px; border-radius:50%; margin-top:2px; }
    .legend { display:flex; gap:12px; flex-wrap:wrap; margin-top:8px; }
    .legend-item { display:flex; align-items:center; gap:4px; font-size:10px; color:#666; }
    .leg-dot { width:8px; height:8px; border-radius:50%; display:inline-block; }
  `]
})
export class LcExpiryHeatmapComponent implements OnInit {
  @Input() data: any[] = [];

  private store = inject(MessageStore);
  months: { label: string; cells: CalDay[] }[] = [];
  dayHeaders = ['Mon','Tue','Wed','Thu','Fri','Sat','Sun'];

  ngOnInit(): void {
    const today = new Date();
    const months = [
      new Date(today.getFullYear(), today.getMonth(), 1),
      new Date(today.getFullYear(), today.getMonth() + 1, 1),
    ];

    // Build a date→data map from input
    const dataMap = new Map<string, DayData>();
    this.data.forEach(d => {
      const raw = d['ExpiryDate'] ?? d['LcExpiryDate'];
      if (!raw) return;
      const dt = new Date(raw);
      if (isNaN(dt.getTime())) return;
      const key = this.dateKey(dt);
      const urgency = this.computeUrgency(dt, today, d['UrgencyLevel']);
      dataMap.set(key, {
        date: dt,
        count: d['LcCount'] ?? 1,
        urgency,
        lcNumbers: d['LcNumbers'] ?? d['LcNumber'] ?? '',
      });
    });

    this.months = months.map(m => {
      const label = m.toLocaleDateString('en-GB', { month:'long', year:'numeric' });
      const firstDay = new Date(m.getFullYear(), m.getMonth(), 1);
      // Mon=0 … Sun=6
      let startOffset = (firstDay.getDay() + 6) % 7;
      const daysInMonth = new Date(m.getFullYear(), m.getMonth() + 1, 0).getDate();

      const cells: CalDay[] = [];
      for (let i = 0; i < startOffset; i++) cells.push({ day:null, data:null });
      for (let d = 1; d <= daysInMonth; d++) {
        const dt = new Date(m.getFullYear(), m.getMonth(), d);
        const key = this.dateKey(dt);
        cells.push({ day:d, data: dataMap.get(key) ?? null });
      }
      return { label, cells };
    });
  }

  private dateKey(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
  }

  private computeUrgency(dt: Date, today: Date, hint?: string): Urgency {
    if (hint) return hint as Urgency;
    const diff = Math.ceil((dt.getTime() - today.getTime()) / 86400000);
    if (diff < 0)  return 'expired';
    if (diff < 7)  return 'critical';
    if (diff < 30) return 'warning';
    return 'normal';
  }

  urgencyStyle(u: Urgency) { return URGENCY_COLORS[u] ?? URGENCY_COLORS.none; }

  formatDate(d: Date): string {
    return d.toLocaleDateString('en-GB', { day:'2-digit', month:'short', year:'numeric' });
  }

  sendQuery(q: string): void { this.store.sendMessage(q); }
}
