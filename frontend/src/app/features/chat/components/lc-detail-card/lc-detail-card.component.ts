import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-lc-detail-card',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (data) {
      <div class="detail-card">

        <!-- Header: LC Number + Status badge -->
        <div class="detail-header">
          <div class="lc-identity">
            <span class="lc-number">{{ data['LcNumber'] ?? data['ContractNumber'] ?? 'LC Request' }}</span>
            <span class="bank-tag">{{ data['Bank'] ?? data['IssuingBank'] ?? '—' }}</span>
          </div>
          <span class="status-pill" [ngClass]="getStatusClass(data['Status'])">
            {{ data['StatusLabel'] ?? formatStatus(data['Status']) }}
          </span>
        </div>

        <!-- Amount hero -->
        @if (data['LcAmount'] || data['RequestedAmount']) {
          <div class="amount-hero">
            <span class="amount-value">{{ formatAmount(data['LcAmount'] ?? data['RequestedAmount'], data['Currency']) }}</span>
            <span class="amount-label">{{ data['LcAmount'] ? 'LC amount' : 'Requested amount' }}</span>
          </div>
        }

        <!-- Two-column grid of key fields -->
        <div class="fields-grid">
          @if (data['CustomerName']) {
            <span class="field-label">Customer</span>
            <span class="field-value">{{ data['CustomerName'] }}</span>
          }
          @if (data['Product']) {
            <span class="field-label">Product</span>
            <span class="field-value">{{ data['Product'] }}</span>
          }
          @if (data['TypeOfLC'] ?? data['TypeOfLcRequested']) {
            <span class="field-label">Type</span>
            <span class="field-value">{{ data['TypeOfLC'] ?? data['TypeOfLcRequested'] }}</span>
          }
          @if (data['PaymentTerms']) {
            <span class="field-label">Payment terms</span>
            <span class="field-value">{{ data['PaymentTerms'] }}</span>
          }
          @if (data['LcIssueDate']) {
            <span class="field-label">Issue date</span>
            <span class="field-value">{{ formatDate(data['LcIssueDate']) }}</span>
          }
          @if (data['LcExpiryDate']) {
            <span class="field-label">Expiry date</span>
            <span class="field-value" [class.date-danger]="isDatePast(data['LcExpiryDate'])">
              {{ formatDate(data['LcExpiryDate']) }}
              @if (isDatePast(data['LcExpiryDate'])) {
                <span class="expired-tag">Expired</span>
              }
            </span>
          }
          @if (data['GracePeriod']) {
            <span class="field-label">Grace period</span>
            <span class="field-value" [class.date-warning]="isDateSoon(data['GracePeriod'])">{{ formatDate(data['GracePeriod']) }}</span>
          }
          @if (data['AmiPaymentDate']) {
            <span class="field-label">AMI payment date</span>
            <span class="field-value">{{ formatDate(data['AmiPaymentDate']) }}</span>
          }
          @if (data['BeneficiaryOnLC']) {
            <span class="field-label">Beneficiary</span>
            <span class="field-value">{{ data['BeneficiaryOnLC'] }}</span>
          }
          @if (data['PortOfLoading']) {
            <span class="field-label">Port of loading</span>
            <span class="field-value">{{ data['PortOfLoading'] }}</span>
          }
          @if (data['PortOfDischarge']) {
            <span class="field-label">Port of discharge</span>
            <span class="field-value">{{ data['PortOfDischarge'] }}</span>
          }
          @if (data['BusinessUnit']) {
            <span class="field-label">Business unit</span>
            <span class="field-value">{{ data['BusinessUnit'] }}</span>
          }
          @if (data['QuantityMt']) {
            <span class="field-label">Quantity (MT)</span>
            <span class="field-value">{{ formatNumber(data['QuantityMt']) }}</span>
          }
          @if (data['UsdBankCharges']) {
            <span class="field-label">Bank charges (USD)</span>
            <span class="field-value">{{ formatAmount(data['UsdBankCharges'], 'USD') }}</span>
          }
          @if (data['SapOrderNumber']) {
            <span class="field-label">SAP order</span>
            <span class="field-value">{{ data['SapOrderNumber'] }}</span>
          }
          @if (asNumber(data['AmendmentCount']) > 0) {
            <span class="field-label">Amendments</span>
            <span class="field-value amendment-count">{{ data['AmendmentCount'] }} amendment{{ asNumber(data['AmendmentCount']) > 1 ? 's' : '' }}</span>
          }
          @if (data['RequestCreatedOn']) {
            <span class="field-label">Requested on</span>
            <span class="field-value">{{ formatDate(data['RequestCreatedOn']) }}</span>
          }
        </div>

        <!-- Comment if present -->
        @if (data['Comment']) {
          <div class="comment-section">
            <span class="field-label">Comment</span>
            <p class="comment-text">{{ data['Comment'] }}</p>
          </div>
        }

      </div>
    }
  `,
  styles: [`
    .detail-card { border: 0.5px solid var(--color-border-tertiary, #e5e7eb); border-radius: 12px; padding: 16px; display: flex; flex-direction: column; gap: 14px; background: var(--color-background-primary, #fff); }
    .detail-header { display: flex; justify-content: space-between; align-items: flex-start; gap: 8px; }
    .lc-identity { display: flex; flex-direction: column; gap: 4px; }
    .lc-number { font-size: 16px; font-weight: 500; color: var(--color-text-primary, #111827); }
    .bank-tag { font-size: 11px; color: var(--color-text-secondary, #6b7280); background: var(--color-background-secondary, #f8fafc); padding: 2px 8px; border-radius: 8px; display: inline-block; }
    .status-pill { font-size: 12px; font-weight: 500; padding: 4px 12px; border-radius: 20px; white-space: nowrap; }
    .status-issued { background: #D1ECF1; color: #0C5460; }
    .status-paid { background: #D4EDDA; color: #155724; }
    .status-pending { background: #FFF3CD; color: #856404; }
    .status-rejected { background: #F8D7DA; color: #721C24; }
    .status-draft { background: #E2E3E5; color: #383D41; }
    .status-default { background: var(--color-background-secondary, #f8fafc); color: var(--color-text-secondary, #6b7280); }
    .amount-hero { display: flex; flex-direction: column; gap: 2px; padding: 12px 14px; background: var(--color-background-secondary, #f8fafc); border-radius: 8px; }
    .amount-value { font-size: 22px; font-weight: 500; color: var(--color-text-primary, #111827); font-variant-numeric: tabular-nums; }
    .amount-label { font-size: 12px; color: var(--color-text-secondary, #6b7280); }
    .fields-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 4px 16px; }
    .field-label { font-size: 12px; color: var(--color-text-secondary, #6b7280); padding: 3px 0; }
    .field-value { font-size: 13px; color: var(--color-text-primary, #111827); padding: 3px 0; }
    .date-danger { color: #721C24; }
    .date-warning { color: #856404; }
    .expired-tag { font-size: 10px; background: #F8D7DA; color: #721C24; padding: 1px 6px; border-radius: 8px; margin-left: 4px; }
    .amendment-count { color: #854F0B; font-weight: 500; }
    .comment-section { border-top: 0.5px solid var(--color-border-tertiary, #e5e7eb); padding-top: 10px; display: flex; flex-direction: column; gap: 4px; }
    .comment-text { font-size: 13px; color: var(--color-text-secondary, #6b7280); line-height: 1.5; margin: 0; }
  `]
})
export class LcDetailCardComponent {
  @Input() data: Record<string, unknown> | null = null;

  asNumber(v: unknown): number { return parseFloat(String(v ?? 0)) || 0; }

  getStatusClass(status: unknown): string {
    const map: Record<string, string> = {
      'LCIssued': 'status-issued', 'PaymentDone': 'status-paid',
      'Submitted_N+1': 'status-pending', 'Submitted_N+2': 'status-pending',
      'Submitted_For_Validation': 'status-pending', 'Draft': 'status-draft',
      'Rejected': 'status-rejected', 'Cancelled': 'status-rejected',
    };
    return map[String(status ?? '')] ?? 'status-default';
  }

  formatStatus(status: unknown): string {
    const map: Record<string, string> = {
      'Submitted_N+1': 'Pending N+1', 'Submitted_N+2': 'Pending N+2',
      'Submitted_For_Validation': 'In Validation', 'LCIssued': 'Issued',
      'PaymentDone': 'Paid', 'PaymentNotDone': 'Unpaid',
    };
    const s = String(status ?? '');
    return map[s] ?? (s || '—');
  }

  formatAmount(value: unknown, currency?: unknown): string {
    if (value == null) return '—';
    const n = parseFloat(String(value));
    if (isNaN(n)) return '—';
    const formatted = n >= 1_000_000
      ? (n / 1_000_000).toFixed(2) + 'M'
      : new Intl.NumberFormat('en-US').format(Math.round(n));
    return currency ? `${currency} ${formatted}` : formatted;
  }

  formatDate(value: unknown): string {
    if (!value) return '—';
    const d = new Date(String(value));
    if (isNaN(d.getTime()) || d.getFullYear() < 2000) return '—';
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  }

  formatNumber(value: unknown): string {
    if (value == null) return '—';
    return new Intl.NumberFormat('en-US').format(Math.round(parseFloat(String(value))));
  }

  isDatePast(value: unknown): boolean {
    if (!value) return false;
    return new Date(String(value)) < new Date();
  }

  isDateSoon(value: unknown): boolean {
    if (!value) return false;
    const d = new Date(String(value));
    const diff = (d.getTime() - Date.now()) / 86400000;
    return diff >= 0 && diff <= 14;
  }
}
