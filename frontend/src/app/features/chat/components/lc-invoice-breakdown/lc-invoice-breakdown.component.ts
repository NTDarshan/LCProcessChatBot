import { Component, Input, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-lc-invoice-breakdown',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (data.length > 0) {
      <div class="inv-wrap">

        <!-- Summary row -->
        <div class="inv-summary">
          <div class="sum-card">
            <span class="sum-label">Total invoices</span>
            <span class="sum-value">{{ data.length }}</span>
          </div>
          <div class="sum-card paid">
            <span class="sum-label">Paid</span>
            <span class="sum-value">{{ paidCount }}</span>
          </div>
          <div class="sum-card unpaid">
            <span class="sum-label">Outstanding</span>
            <span class="sum-value">{{ unpaidCount }}</span>
          </div>
          <div class="sum-card total">
            <span class="sum-label">Total value</span>
            <span class="sum-value">{{ formatAmount(totalValue, primaryCurrency) }}</span>
          </div>
        </div>

        <!-- Invoice rows -->
        <div class="inv-list">
          @for (inv of data; track $index) {
            <div class="inv-row">
              <div class="inv-row-header">
                <span class="doc-set">{{ inv['DocumentSetNumber'] ?? inv['LcNumber'] ?? 'Invoice' }}</span>
                <span class="paid-badge" [class.is-paid]="inv['IsPaid']" [class.is-unpaid]="!inv['IsPaid']">
                  {{ inv['IsPaid'] ? 'Paid' : 'Outstanding' }}
                </span>
                @if (inv['IsRefunded']) {
                  <span class="refund-badge">Refunded</span>
                }
              </div>
              <div class="inv-row-body">
                <span class="inv-label">Amount</span>
                <span class="inv-val amount">{{ formatAmount(inv['InvoiceAmount'], inv['Currency']) }}</span>
                @if (inv['InvoiceDate']) {
                  <span class="inv-label">Invoice date</span>
                  <span class="inv-val">{{ formatDate(inv['InvoiceDate']) }}</span>
                }
                @if (inv['ShipmentDate']) {
                  <span class="inv-label">Shipment date</span>
                  <span class="inv-val">{{ formatDate(inv['ShipmentDate']) }}</span>
                }
                @if (inv['ExpectedPaymentDate']) {
                  <span class="inv-label">Expected payment</span>
                  <span class="inv-val">{{ formatDate(inv['ExpectedPaymentDate']) }}</span>
                }
                @if (inv['BankCharges']) {
                  <span class="inv-label">Bank charges</span>
                  <span class="inv-val">USD {{ formatAmount(inv['BankCharges'], '') }}</span>
                }
                @if (inv['RefundValueDate']) {
                  <span class="inv-label">Refund date</span>
                  <span class="inv-val">{{ formatDate(inv['RefundValueDate']) }}</span>
                }
              </div>
            </div>
          }
        </div>

      </div>
    }
  `,
  styles: [`
    .inv-wrap { display: flex; flex-direction: column; gap: 12px; }
    .inv-summary { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; }
    .sum-card { background: var(--color-background-secondary, #f8fafc); border-radius: 8px; padding: 10px 12px; display: flex; flex-direction: column; gap: 4px; }
    .sum-card.paid { background: #D4EDDA; }
    .sum-card.unpaid { background: #FFF3CD; }
    .sum-card.total { background: #E6F1FB; }
    .sum-label { font-size: 11px; color: var(--color-text-secondary, #6b7280); }
    .sum-value { font-size: 18px; font-weight: 500; color: var(--color-text-primary, #111827); }
    .sum-card.paid .sum-value { color: #155724; }
    .sum-card.unpaid .sum-value { color: #856404; }
    .sum-card.total .sum-value { color: #0C447C; font-size: 14px; }
    .inv-list { display: flex; flex-direction: column; gap: 8px; }
    .inv-row { border: 0.5px solid var(--color-border-tertiary, #e5e7eb); border-radius: 8px; overflow: hidden; }
    .inv-row-header { display: flex; align-items: center; gap: 8px; padding: 8px 12px; background: var(--color-background-secondary, #f8fafc); border-bottom: 0.5px solid var(--color-border-tertiary, #e5e7eb); }
    .doc-set { font-size: 13px; font-weight: 500; color: var(--color-text-primary, #111827); flex: 1; }
    .paid-badge { font-size: 11px; font-weight: 500; padding: 2px 8px; border-radius: 10px; }
    .paid-badge.is-paid { background: #D4EDDA; color: #155724; }
    .paid-badge.is-unpaid { background: #FFF3CD; color: #856404; }
    .refund-badge { font-size: 10px; padding: 2px 7px; border-radius: 8px; background: #E6F1FB; color: #0C447C; }
    .inv-row-body { display: grid; grid-template-columns: 1fr 1fr; gap: 0; padding: 8px 12px; }
    .inv-label { font-size: 12px; color: var(--color-text-secondary, #6b7280); padding: 2px 0; }
    .inv-val { font-size: 12px; color: var(--color-text-primary, #111827); padding: 2px 0; }
    .inv-val.amount { font-weight: 500; font-variant-numeric: tabular-nums; }
  `]
})
export class LcInvoiceBreakdownComponent implements OnInit {
  @Input() data: Record<string, unknown>[] = [];

  paidCount = 0;
  unpaidCount = 0;
  totalValue = 0;
  primaryCurrency = '';

  ngOnInit() {
    this.paidCount      = this.data.filter(i => i['IsPaid']).length;
    this.unpaidCount    = this.data.filter(i => !i['IsPaid']).length;
    this.totalValue     = this.data.reduce((s, i) => s + (parseFloat(String(i['InvoiceAmount'] ?? 0)) || 0), 0);
    this.primaryCurrency = String(this.data[0]?.['Currency'] ?? '');
  }

  formatAmount(value: unknown, currency: unknown): string {
    if (value == null || value === '') return '—';
    const n = parseFloat(String(value));
    if (isNaN(n)) return '—';
    const f = new Intl.NumberFormat('en-US').format(Math.round(n));
    return currency ? `${currency} ${f}` : f;
  }

  formatDate(value: unknown): string {
    if (!value) return '—';
    const d = new Date(String(value));
    if (isNaN(d.getTime()) || d.getFullYear() < 2000) return '—';
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  }
}
