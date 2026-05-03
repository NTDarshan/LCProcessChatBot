import { Component, Input } from '@angular/core';

const INTENT_HINTS: Record<string, string> = {
  ExpiredLC:     'No LCs have expired yet or the lc_expired flag is not set.',
  TopBanks:      'No LC data available for bank comparison.',
  ExpiringLC:    'No LCs are expiring within the selected time range.',
  OutstandingLC: 'No outstanding LCs with unpaid invoices found.',
};

@Component({
  selector: 'app-lc-empty-state',
  standalone: true,
  template: `
    <div class="empty-state">
      <div class="empty-icon">
        <svg width="40" height="40" viewBox="0 0 40 40" fill="none">
          <circle cx="18" cy="18" r="11" stroke="#9ca3af" stroke-width="2"/>
          <line x1="26" y1="26" x2="34" y2="34" stroke="#9ca3af" stroke-width="2" stroke-linecap="round"/>
          <line x1="13" y1="18" x2="23" y2="18" stroke="#9ca3af" stroke-width="2" stroke-linecap="round"/>
          <line x1="18" y1="13" x2="18" y2="23" stroke="#9ca3af" stroke-width="2" stroke-linecap="round"/>
        </svg>
      </div>
      <p class="empty-primary">No records found</p>
      <p class="empty-secondary">{{ hint }}</p>
    </div>
  `,
  styles: [`
    .empty-state {
      display: flex; flex-direction: column; align-items: center;
      gap: 6px; padding: 24px 16px; border-radius: 12px;
      background: #f9fafb; border: 1px solid #f3f4f6;
      margin-top: 8px; text-align: center;
    }
    .empty-icon { opacity: 0.6; }
    .empty-primary  { font-size: 14px; font-weight: 600; color: #6b7280; margin: 0; }
    .empty-secondary { font-size: 12px; color: #9ca3af; margin: 0; max-width: 280px; }
  `]
})
export class LcEmptyStateComponent {
  @Input() intent = '';

  get hint(): string {
    return INTENT_HINTS[this.intent] ?? 'Try adjusting your search filters or time range.';
  }
}
