import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

const COLUMN_LABELS: Record<string, string> = {
  LcNumber: 'LC Number', Bank: 'Bank', CustomerName: 'Customer',
  SapCustomerName: 'SAP Name', LcAmount: 'LC Amount', RequestedAmount: 'Req. Amount',
  Currency: 'Currency', TypeOfLC: 'Type', TypeOfLcRequested: 'Type',
  LcIssueDate: 'Issue Date', LcExpiryDate: 'Expiry Date', GracePeriod: 'Grace Period',
  IsExpired: 'Expired', AmiPaymentDate: 'Pmt Date', BeneficiaryOnLC: 'Beneficiary',
  PaymentTerms: 'Payment Terms', PortOfLoading: 'Loading Port',
  PortOfDischarge: 'Discharge Port', ActualShipmentDate: 'Shipment',
  PlannedShipmentDate: 'Planned Shipment', RequestCreatedOn: 'Created',
  Status: 'Status', Product: 'Product', RequestId: 'ID',
  LcCount: 'LC Count', TotalLcValue: 'Total Value', IssuedCount: 'Issued',
  PaidCount: 'Paid', PendingCount: 'Pending',
  AmendmentId: 'Amend ID', AmendedAmount: 'Amended Amount',
  NewExpiryDate: 'New Expiry', NewPaymentTerms: 'New Terms',
  AmendedBy: 'Amended By', AmendedOn: 'Amended On',
  AmendmentCount: 'Amendments', LastAmendedOn: 'Last Amended',
  InvoiceAmount: 'Invoice Amt', InvoiceDate: 'Invoice Date',
  IsPaid: 'Paid?', IsFinalUpdate: 'Final?', IsRefunded: 'Refunded?',
  ExpectedPaymentDate: 'Expected Pmt', BankCharges: 'Bank Charges',
  Action: 'Action', LogType: 'Type', ActionedBy: 'By', ActionedOn: 'When', Comment: 'Comment',
  InvoiceId: 'Invoice ID', BusinessUnit: 'BU', SupplierName: 'Supplier',
  LcStatusDetail: 'LC Status', ContractNumber: 'Contract No', Volume: 'Volume',
};

const SKIP_KEYS = new Set([
  'LcNumber', 'ContractNumber', 'Bank', 'IssuingBank', 'Status', 'StatusLabel',
  'LcAmount', 'RequestedAmount', 'Currency', 'Comment', 'RequestId', 'obj_id',
]);

const PRIORITY_KEYS = [
  'CustomerName', 'Product', 'TypeOfLC', 'TypeOfLcRequested', 'PaymentTerms', 'BusinessUnit',
];

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
          @for (field of dynamicFields; track field.key) {
            <span class="field-label">{{ field.label }}</span>
            @if (field.key === 'LcExpiryDate') {
              <span class="field-value" [class.date-danger]="isDatePast(data![field.key])">
                {{ formatDate(data![field.key]) }}
                @if (isDatePast(data![field.key])) {
                  <span class="expired-tag">Expired</span>
                }
              </span>
            } @else if (field.key === 'GracePeriod') {
              <span class="field-value" [class.date-warning]="isDateSoon(data![field.key])">{{ formatDate(data![field.key]) }}</span>
            } @else if (isDateKey(field.key)) {
              <span class="field-value">{{ formatDate(data![field.key]) }}</span>
            } @else if (isAmountKey(field.key)) {
              <span class="field-value">{{ formatAmount(data![field.key], data!['Currency']) }}</span>
            } @else {
              <span class="field-value">{{ field.value }}</span>
            }
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

  get dynamicFields(): { key: string; label: string; value: string }[] {
    if (!this.data) return [];
    const d = this.data;

    const keys = Object.keys(d).filter(k => {
      if (SKIP_KEYS.has(k)) return false;
      const v = d[k];
      return v !== null && v !== undefined && v !== '';
    });

    const prioritySet = new Set(PRIORITY_KEYS);
    const priorityKeys = PRIORITY_KEYS.filter(k => keys.includes(k));
    const rest = keys.filter(k => !prioritySet.has(k));
    const dateKeys   = rest.filter(k =>  this.isDateKey(k) && !this.isAmountKey(k)).sort();
    const amountKeys = rest.filter(k => !this.isDateKey(k) &&  this.isAmountKey(k)).sort();
    const otherKeys  = rest.filter(k => !this.isDateKey(k) && !this.isAmountKey(k)).sort();

    return [...priorityKeys, ...dateKeys, ...amountKeys, ...otherKeys].map(k => ({
      key:   k,
      label: COLUMN_LABELS[k] ?? k.replace(/([A-Z])/g, ' $1').trim(),
      value: String(d[k] ?? ''),
    }));
  }

  isDateKey(key: string): boolean   { return /Date$|On$/.test(key); }
  isAmountKey(key: string): boolean { return /Amount|Value|Charges|Total/i.test(key); }

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
    let formatted: string;
    if (n >= 1_000_000_000) formatted = (n / 1_000_000_000).toFixed(2) + 'B';
    else if (n >= 1_000_000) formatted = (n / 1_000_000).toFixed(2) + 'M';
    else formatted = new Intl.NumberFormat('en-US').format(Math.round(n));
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
