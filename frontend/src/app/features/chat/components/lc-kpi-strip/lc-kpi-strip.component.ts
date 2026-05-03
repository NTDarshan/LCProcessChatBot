import {
  Component, Input, OnInit, OnDestroy, ElementRef,
  ChangeDetectionStrategy, inject
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { MessageStore } from '../../services/message.store';

interface KpiCard {
  key: string;
  label: string;
  color: string;
  accent: string;
  format: 'number' | 'currency';
  clickQuery?: string;
  value: number;
  display: string;
}

function fmtAmount(v: number): string {
  if (v >= 1_000_000_000) return `${(v / 1_000_000_000).toFixed(1)}b`;
  if (v >= 1_000_000)     return `${(v / 1_000_000).toFixed(1)}m`;
  if (v >= 1_000)         return `${(v / 1_000).toFixed(1)}k`;
  return v.toString();
}

const CARD_DEFS: Omit<KpiCard, 'value'|'display'>[] = [
  { key:'TotalActiveLCs',    label:'Active LCs',         color:'#185FA5', accent:'rgba(24,95,165,0.10)',   format:'number' },
  { key:'TotalExposure',     label:'Total Exposure',     color:'#2A8C7A', accent:'rgba(42,140,122,0.10)',  format:'currency', },
  { key:'ExpiringIn30Days',  label:'Expiring in 30 Days',color:'#D85A30', accent:'rgba(216,90,48,0.10)',   format:'number',  clickQuery:'Show LCs expiring in next 30 days' },
  { key:'UnpaidInvoices',    label:'Unpaid Invoices',    color:'#E24B4A', accent:'rgba(226,75,74,0.10)',   format:'number' },
  { key:'Overdue',           label:'Overdue',            color:'#721C24', accent:'rgba(114,28,36,0.10)',   format:'number' },
];

@Component({
  selector: 'app-lc-kpi-strip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="kpi-strip">
      <div class="kpi-header">
        <span class="kpi-title">Portfolio Dashboard</span>
        <span class="kpi-badge">KPI Strip</span>
      </div>
      <div class="kpi-grid">
        @for (card of cards; track card.key) {
          <div class="kpi-card"
            [style.border-top-color]="card.color"
            [style.cursor]="card.clickQuery ? 'pointer' : 'default'"
            [title]="card.clickQuery ? 'Click to explore →' : ''"
            (click)="card.clickQuery && sendQuery(card.clickQuery)">
            <span class="kpi-label">{{ card.label }}</span>
            <span class="kpi-value" [style.color]="card.color">{{ card.display }}</span>
            @if (card.format === 'currency') {
              <span class="kpi-context">EUR</span>
            }
            @if (card.clickQuery) {
              <span class="kpi-arrow" [style.color]="card.color">Explore →</span>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    .kpi-strip { padding:4px 0; }
    .kpi-header { display:flex; align-items:center; gap:8px; margin-bottom:12px; }
    .kpi-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .kpi-badge { font-size:10px; padding:2px 8px; border-radius:99px;
      background:rgba(24,95,165,0.12); color:#185FA5; font-weight:600; }
    .kpi-grid {
      display:grid;
      grid-template-columns:repeat(3,1fr);
      gap:10px;
    }
    @media(max-width:520px) { .kpi-grid { grid-template-columns:repeat(2,1fr); } }
    .kpi-card {
      padding:12px 14px;
      border-radius:10px;
      background:var(--color-background-secondary,#f8f9fc);
      border:1px solid rgba(0,0,0,0.07);
      border-top:3px solid;
      display:flex; flex-direction:column; gap:3px;
      transition:background 0.15s;
    }
    .kpi-card:hover { background:var(--color-background-hover,#eef1f8); }
    .kpi-label { font-size:11px; color:#888; font-weight:500; }
    .kpi-value { font-size:26px; font-weight:700; line-height:1.1; letter-spacing:-0.5px; }
    .kpi-context { font-size:10px; color:#aaa; }
    .kpi-arrow { font-size:10px; font-weight:600; margin-top:4px; }
  `]
})
export class LcKpiStripComponent implements OnInit {
  @Input() data: any[] = [];

  private store = inject(MessageStore);
  cards: KpiCard[] = [];

  ngOnInit(): void {
    const row = this.data[0] ?? {};
    this.cards = CARD_DEFS
      .filter(def => row[def.key] !== undefined)
      .map(def => {
        const v = Number(row[def.key] ?? 0);
        return {
          ...def,
          value: v,
          display: def.format === 'currency' ? fmtAmount(v) : v.toLocaleString(),
        };
      });
  }

  sendQuery(q: string): void { this.store.sendMessage(q); }
}
