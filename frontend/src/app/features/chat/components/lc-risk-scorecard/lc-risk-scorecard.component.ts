import { Component, Input, OnInit, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';

interface RiskRow {
  raw: any;
  score: number;
  level: 'Critical' | 'High' | 'Medium' | 'Low';
  factors: string[];
  lc: string;
  bank: string;
  customer: string;
}

const RISK_LEVELS = {
  Critical: { bg:'#F8D7DA', text:'#721C24', min:70 },
  High:     { bg:'#FAEEDA', text:'#712B13', min:40 },
  Medium:   { bg:'#FFF3CD', text:'#856404', min:20 },
  Low:      { bg:'#EAF3DE', text:'#27500A', min:0  },
};

function riskLevel(score: number): 'Critical'|'High'|'Medium'|'Low' {
  if (score >= 70) return 'Critical';
  if (score >= 40) return 'High';
  if (score >= 20) return 'Medium';
  return 'Low';
}

@Component({
  selector: 'app-lc-risk-scorecard',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="risk-wrap">
      <div class="risk-header">
        <span class="risk-title">Risk Scorecard</span>
        <div class="summary-chips">
          <span class="chip critical">{{ counts.Critical }} Critical</span>
          <span class="chip high">{{ counts.High }} High</span>
          <span class="chip medium">{{ counts.Medium }} Medium</span>
        </div>
      </div>
      <div class="risk-list">
        @for (row of riskRows; track row.lc + row.bank) {
          <div class="risk-row">
            <div class="risk-left">
              <span class="level-badge"
                [style.background]="levelStyle(row.level).bg"
                [style.color]="levelStyle(row.level).text">
                {{ row.level }}
              </span>
              <div class="lc-info">
                <span class="lc-id">{{ row.lc || 'LC-' + row.raw['RequestId'] }}</span>
                <span class="lc-meta">{{ row.bank }} · {{ row.customer }}</span>
              </div>
            </div>
            <div class="risk-right">
              <span class="score-num" [style.color]="levelStyle(row.level).text">{{ row.score }}</span>
              <div class="factor-badges">
                @for (f of row.factors; track f) {
                  <span class="factor">{{ f }}</span>
                }
              </div>
            </div>
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    .risk-wrap { padding:4px 0; }
    .risk-header { display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:8px; margin-bottom:12px; }
    .risk-title { font-size:13px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .summary-chips { display:flex; gap:6px; }
    .chip { font-size:10px; font-weight:700; padding:3px 10px; border-radius:99px; }
    .chip.critical { background:#F8D7DA; color:#721C24; }
    .chip.high     { background:#FAEEDA; color:#712B13; }
    .chip.medium   { background:#FFF3CD; color:#856404; }
    .risk-list { display:flex; flex-direction:column; gap:6px; }
    .risk-row {
      display:flex; justify-content:space-between; align-items:center;
      padding:8px 12px; border-radius:8px;
      background:var(--color-background-secondary,#f8f9fc);
      border:1px solid rgba(0,0,0,0.06);
    }
    .risk-left { display:flex; align-items:center; gap:10px; }
    .level-badge { font-size:10px; font-weight:700; padding:3px 9px; border-radius:6px; white-space:nowrap; }
    .lc-info { display:flex; flex-direction:column; }
    .lc-id { font-size:12px; font-weight:600; color:var(--color-text-primary,#1a1a2e); }
    .lc-meta { font-size:11px; color:#888; }
    .risk-right { display:flex; align-items:center; gap:8px; }
    .score-num { font-size:20px; font-weight:700; min-width:36px; text-align:right; }
    .factor-badges { display:flex; flex-wrap:wrap; gap:3px; }
    .factor { font-size:9px; padding:2px 6px; border-radius:4px; background:rgba(0,0,0,0.06); color:#555; }
  `]
})
export class LcRiskScorecardComponent implements OnInit {
  @Input() data: any[] = [];

  riskRows: RiskRow[] = [];
  counts = { Critical:0, High:0, Medium:0, Low:0 };

  ngOnInit(): void {
    const today = new Date();

    this.riskRows = this.data.map(d => {
      let score = 0;
      const factors: string[] = [];
      const days = d['DaysPending'] ?? 0;

      if (days > 10)       { score += 30; factors.push(`${days}d pending`); }
      else if (days > 5)   { score += 15; factors.push(`${days}d pending`); }

      if (d['IsExpired'] === true || d['IsExpired'] === 1) { score += 40; factors.push('Expired'); }

      const amd = d['AmendmentCount'] ?? 0;
      if (amd > 2)         { score += 20; factors.push(`${amd} amendments`); }
      else if (amd > 0)    { score += 10; factors.push(`${amd} amendment`); }

      if ((d['InvoiceCount'] ?? 0) > 0 && !d['IsPaid']) { score += 25; factors.push('Unpaid invoice'); }

      if (d['GracePeriod']) {
        const gp = new Date(d['GracePeriod']);
        if (!isNaN(gp.getTime()) && gp < today) { score += 35; factors.push('Grace passed'); }
      }

      const level = riskLevel(score);
      return {
        raw: d,
        score,
        level,
        factors,
        lc: d['LcNumber'] ?? '',
        bank: d['Bank'] ?? '',
        customer: d['CustomerName'] ?? '',
      };
    })
    .sort((a, b) => b.score - a.score)
    .slice(0, 15);

    this.counts = { Critical:0, High:0, Medium:0, Low:0 };
    this.riskRows.forEach(r => this.counts[r.level]++);
  }

  levelStyle(level: string) { return RISK_LEVELS[level as keyof typeof RISK_LEVELS] ?? RISK_LEVELS.Low; }
}
