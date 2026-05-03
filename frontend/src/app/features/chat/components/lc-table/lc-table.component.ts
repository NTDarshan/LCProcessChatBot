import { Component, Input, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';

interface ColDef { key: string; label: string; type: string; }

const INTENT_COLUMN_MAP: Record<string, string[]> = {
  IssuedLC:          ['LcNumber','Bank','CustomerName','LcAmount','Currency','TypeOfLC','LcIssueDate','LcExpiryDate','GracePeriod','BeneficiaryOnLC','PaymentTerms'],
  PaidLC:            ['LcNumber','Bank','CustomerName','LcAmount','Currency','TypeOfLC','AmiPaymentDate','LcIssueDate','BeneficiaryOnLC'],
  DraftLC:           ['RequestId','Bank','Product','CustomerName','RequestedAmount','Currency','TypeOfLcRequested','PlannedShipmentDate','RequestCreatedOn','Status'],
  RejectedLC:        ['RequestId','Bank','Product','CustomerName','RequestedAmount','Currency','Status','RequestCreatedOn'],
  CancelledLC:       ['RequestId','Bank','Product','CustomerName','RequestedAmount','Currency','Status','RequestCreatedOn'],
  ValidationPending: ['RequestId','Bank','Product','CustomerName','RequestedAmount','Currency','Status','RequestCreatedOn'],
  ExpiringLC:        ['LcNumber','Bank','CustomerName','LcAmount','Currency','LcExpiryDate','GracePeriod','IsExpired','PaymentTerms','TypeOfLC'],
  ExpiredLC:         ['LcNumber','Bank','CustomerName','LcAmount','Currency','LcExpiryDate','GracePeriod','BeneficiaryOnLC'],
  OutstandingLC:     ['LcNumber','Bank','CustomerName','LcAmount','Currency','LcExpiryDate','InvoiceAmount','IsPaid','ExpectedPaymentDate','BankCharges'],
  DelayedRequests:   ['RequestId','Bank','Product','CustomerName','RequestedAmount','Currency','Status','RequestCreatedOn'],
  AmendmentRequests: ['AmendmentId','LcNumber','Bank','CustomerName','AmendedAmount','Currency','NewExpiryDate','NewPaymentTerms','AmendedBy','AmendedOn'],
  InvoiceStatus:     ['LcNumber','Bank','CustomerName','InvoiceAmount','Currency','InvoiceDate','IsPaid','IsFinalUpdate','ExpectedPaymentDate','IsRefunded'],
  CustomerLC:        ['CustomerName','SapCustomerName','LcCount','TotalLcValue','IssuedCount','PaidCount','PendingCount'],
  AmendmentCount:    ['LcNumber','Bank','CustomerName','LcAmount','Currency','AmendmentCount','LastAmendedOn','Status'],
  LcStatus:          ['LcNumber','Bank','CustomerName','LcAmount','Currency','Status','TypeOfLC','LcIssueDate','LcExpiryDate','GracePeriod','PaymentTerms','BeneficiaryOnLC'],
  LcHistory:         ['Action','LogType','ActionedBy','ActionedOn','Comment'],
};

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

const AMOUNT_COLS  = new Set(['LcAmount','RequestedAmount','TotalLcValue','AmendedAmount','InvoiceAmount','BankCharges']);
const DATE_COLS    = new Set(['LcIssueDate','LcExpiryDate','GracePeriod','AmiPaymentDate','ActualShipmentDate','PlannedShipmentDate','RequestCreatedOn','NewExpiryDate','LastAmendedOn','InvoiceDate','ExpectedPaymentDate','AmendedOn','ActionedOn']);
const STATUS_COLS  = new Set(['Status']);
const EXPIRED_COLS = new Set(['IsExpired']);
const BOOLEAN_COLS = new Set(['IsPaid','IsFinalUpdate','IsRefunded']);
const COUNT_COLS   = new Set(['LcCount','IssuedCount','PaidCount','PendingCount','AmendmentCount']);

function colType(key: string): string {
  if (AMOUNT_COLS.has(key))  return 'amount';
  if (DATE_COLS.has(key))    return 'date';
  if (STATUS_COLS.has(key))  return 'status';
  if (EXPIRED_COLS.has(key)) return 'expired';
  if (BOOLEAN_COLS.has(key)) return 'boolean';
  if (COUNT_COLS.has(key))   return 'count';
  return 'text';
}

@Component({
  selector: 'app-lc-table',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (rows.length > 0) {
      <div class="table-wrapper">
        <div class="table-meta">
          <span class="row-count-badge">{{ rows.length }} record{{ rows.length !== 1 ? 's' : '' }}</span>
        </div>
        <div class="table-scroll">
          <table class="lc-table">
            <thead>
              <tr>
                @for (col of visibleColumns; track col.key; let i = $index) {
                  <th [class.sticky-col]="i === 0">{{ col.label }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of displayRows; track $index) {
                <tr>
                  @for (col of visibleColumns; track col.key; let i = $index) {
                    <td [class.sticky-col]="i === 0">
                      @switch (col.type) {
                        @case ('status') {
                          <span class="status-badge" [ngClass]="getStatusClass(row[col.key])">
                            {{ formatStatus(row[col.key]) }}
                          </span>
                        }
                        @case ('amount') {
                          <span class="amount">
                            {{ formatAmount(row[col.key], row['Currency'] ?? row['currency']) }}
                          </span>
                        }
                        @case ('date') {
                          <span class="date">{{ formatDate(row[col.key]) }}</span>
                        }
                        @case ('expired') {
                          <span class="expired-badge" [ngClass]="row[col.key] ? 'exp' : 'act'">
                            {{ row[col.key] ? 'Expired' : 'Active' }}
                          </span>
                        }
                        @case ('boolean') {
                          <span class="bool-badge" [ngClass]="row[col.key] ? 'bool-yes' : 'bool-no'">
                            {{ row[col.key] ? 'Yes' : 'No' }}
                          </span>
                        }
                        @case ('count') {
                          <span class="count-val">{{ row[col.key] ?? '—' }}</span>
                        }
                        @default {
                          <span class="cell-text">{{ row[col.key] ?? '—' }}</span>
                        }
                      }
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (rows.length > 8) {
          <div class="show-more">
            @if (!showAll) {
              <button (click)="showAll = true">Show {{ rows.length - 8 }} more ↓</button>
            } @else {
              <button (click)="showAll = false">Show less ↑</button>
            }
          </div>
        }
      </div>
    }
  `,
  styles: [`
    .table-wrapper { display: flex; flex-direction: column; gap: 6px; margin-top: 8px; }
    .table-meta { display: flex; justify-content: flex-end; }
    .row-count-badge {
      font-size: 11px; background: var(--bg-secondary, #f3f4f6);
      padding: 2px 8px; border-radius: 10px; color: #6b7280;
    }
    .table-scroll { overflow-x: auto; border-radius: 10px; border: 1px solid #e5e7eb; }
    .lc-table { width: max-content; min-width: 100%; border-collapse: collapse; font-size: 13px; table-layout: auto; }
    .lc-table thead tr { background: #f8fafc; }
    .lc-table th {
      padding: 9px 14px; text-align: left; font-weight: 600; font-size: 11px;
      color: #6b7280; white-space: nowrap; border-bottom: 1px solid #e5e7eb;
      text-transform: uppercase; letter-spacing: 0.04em; min-width: 80px;
    }
    .lc-table td { padding: 9px 14px; border-bottom: 1px solid #f3f4f6; vertical-align: middle; white-space: nowrap; }
    .lc-table tbody tr:last-child td { border-bottom: none; }
    .lc-table tbody tr:hover { background: #f9fafb; }
    .sticky-col {
      position: sticky; left: 0; background: #fff; z-index: 1;
      box-shadow: 2px 0 5px rgba(0,0,0,0.06);
    }
    thead .sticky-col { background: #f8fafc; }
    .lc-table tbody tr:hover .sticky-col { background: #f9fafb; }
    .cell-text { font-size: 13px; color: #111827; }
    .amount { font-variant-numeric: tabular-nums; font-weight: 600; color: #111827; white-space: nowrap; }
    .date { white-space: nowrap; color: #6b7280; font-size: 12px; }
    .status-badge { display: inline-block; padding: 3px 9px; border-radius: 20px; font-size: 11px; font-weight: 600; white-space: nowrap; }
    .s-pending    { background: #fef3c7; color: #92400e; }
    .s-issued     { background: #dbeafe; color: #1e40af; }
    .s-paid       { background: #d1fae5; color: #065f46; }
    .s-draft      { background: #f3f4f6; color: #374151; }
    .s-rejected   { background: #fee2e2; color: #991b1b; }
    .s-cancelled  { background: #fce7f3; color: #9d174d; }
    .s-validation { background: #e0e7ff; color: #3730a3; }
    .s-default    { background: #f3f4f6; color: #6b7280; }
    .expired-badge { display: inline-block; padding: 3px 9px; border-radius: 20px; font-size: 11px; font-weight: 600; }
    .exp { background: #fee2e2; color: #991b1b; }
    .act { background: #d1fae5; color: #065f46; }
    .bool-badge { display: inline-block; padding: 3px 9px; border-radius: 20px; font-size: 11px; font-weight: 600; }
    .bool-yes { background: #d1fae5; color: #065f46; }
    .bool-no  { background: #fee2e2; color: #991b1b; }
    .count-val { font-weight: 700; display: block; text-align: right; font-variant-numeric: tabular-nums; color: #111827; }
    .show-more { text-align: center; padding: 8px; }
    .show-more button {
      background: none; border: 1px solid #e5e7eb; padding: 6px 18px;
      border-radius: 6px; font-size: 12px; cursor: pointer; color: #6b7280;
      transition: background 0.15s;
    }
    .show-more button:hover { background: #f9fafb; }
  `]
})
export class LcTableComponent implements OnInit {
  @Input() data: Record<string, unknown>[] = [];
  @Input() intent = '';

  showAll = false;
  visibleColumns: ColDef[] = [];
  rows: Record<string, unknown>[] = [];

  ngOnInit() {
    this.rows = this.data;
    this.buildVisibleColumns();
  }

  get displayRows() { return this.showAll ? this.rows : this.rows.slice(0, 8); }

  private buildVisibleColumns() {
    if (!this.rows.length) return;
    const keys = INTENT_COLUMN_MAP[this.intent];

    if (keys) {
      // Intent-aware: preserve map order, only include cols present & non-null
      this.visibleColumns = keys
        .filter(k => this.rows.some(r => r[k] != null))
        .map(k => ({ key: k, label: COLUMN_LABELS[k] ?? k, type: colType(k) }));
    } else {
      // Fallback: all non-null keys from first row
      const first = this.rows[0];
      this.visibleColumns = Object.keys(first)
        .filter(k => this.rows.some(r => r[k] != null))
        .map(k => ({ key: k, label: COLUMN_LABELS[k] ?? k, type: colType(k) }));
    }
  }

  formatStatus(v: unknown): string {
    const map: Record<string, string> = {
      'Submitted_N+1': 'Pending N+1', 'Submitted_N+2': 'Pending N+2',
      'Submitted_For_Validation': 'Validation', 'LCIssued': 'Issued',
      'PaymentDone': 'Paid', 'PaymentNotDone': 'Unpaid',
      'Draft': 'Draft', 'Rejected': 'Rejected', 'Cancelled': 'Cancelled',
    };
    return map[String(v)] ?? String(v ?? '—');
  }

  getStatusClass(v: unknown): string {
    const s = String(v ?? '');
    if (s.includes('N+1') || s.includes('N+2')) return 's-pending';
    if (s === 'Submitted_For_Validation') return 's-validation';
    if (s === 'LCIssued') return 's-issued';
    if (s === 'PaymentDone') return 's-paid';
    if (s === 'Draft') return 's-draft';
    if (s === 'Rejected') return 's-rejected';
    if (s === 'Cancelled') return 's-cancelled';
    return 's-default';
  }

  formatAmount(v: unknown, currency?: unknown): string {
    if (v == null) return '—';
    const n = parseFloat(String(v));
    if (isNaN(n)) return '—';
    const cur = currency ? `${currency} ` : '';
    if (n >= 1_000_000) return `${cur}${(n / 1_000_000).toFixed(2)}M`;
    if (n >= 1_000)     return `${cur}${(n / 1_000).toFixed(0)}K`;
    return `${cur}${new Intl.NumberFormat('en-US').format(Math.round(n))}`;
  }

  formatDate(v: unknown): string {
    if (!v) return '—';
    const d = new Date(String(v));
    if (isNaN(d.getTime()) || d.getFullYear() < 2000) return '—';
    const day = String(d.getDate()).padStart(2, '0');
    const month = d.toLocaleString('en-GB', { month: 'short' });
    return `${day}-${month}-${d.getFullYear()}`;
  }
}
