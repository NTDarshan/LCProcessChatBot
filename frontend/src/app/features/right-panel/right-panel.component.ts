import { Component, ChangeDetectionStrategy, inject, computed } from '@angular/core';
import { DatePipe } from '@angular/common';
import { KeyInsight } from '../../shared/models/chat.models';
import { MessageStore } from '../chat/services/message.store';

const QUICK_ACTIONS = [
  { label: 'New LC',      icon: 'new'     },
  { label: 'Upload Doc',  icon: 'upload'  },
  { label: 'Help',        icon: 'help'    },
];

@Component({
  selector: 'app-right-panel',
  standalone: true,
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './right-panel.component.html',
  styleUrl:    './right-panel.component.scss',
})
export class RightPanelComponent {
  protected readonly store = inject(MessageStore);
  protected readonly quickActions = QUICK_ACTIONS;

  // The first row returned by the last query – used as "LC Details"
  protected readonly firstRow = computed(() => {
    const rows = this.store.lastResponseData();
    return rows.length > 0 ? (rows[0] as Record<string, unknown>) : null;
  });

  // Total number of matching rows from the last query
  protected readonly rowCount = computed(() => this.store.lastResponseData().length);

  // Sum of lc_amount (or total_amount) across returned rows for key insight
  protected readonly totalValue = computed(() => {
    const rows = this.store.lastResponseData() as Record<string, unknown>[];
    const sum = rows.reduce((acc, r) => {
      const v = r['lc_amount'] ?? r['totalValue'] ?? 0;
      return acc + (typeof v === 'number' ? v : parseFloat(String(v)) || 0);
    }, 0);
    if (sum === 0) return null;
    const m = sum / 1_000_000;
    return `${(rows[0]?.['currency'] as string) ?? ''} ${m.toFixed(2)} M`.trim();
  });

  protected readonly insights = computed((): KeyInsight[] => [
    {
      label: 'Records Found',
      value: this.rowCount(),
      trend: 'neutral',
      trendPercent: 0,
      icon: 'clock',
      color: '#4f46e5'
    },
    {
      label: 'Total LC Value',
      value: this.totalValue() ?? '—',
      trend: 'up',
      trendPercent: 0,
      icon: 'currency',
      color: '#10b981'
    },
  ]);
}
