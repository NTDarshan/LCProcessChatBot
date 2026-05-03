import { Component, Input, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';

type DisplayItem =
  | { type: 'event';      event: Record<string, unknown>; isLast: boolean }
  | { type: 'separator';  dateLabel: string }
  | { type: 'annotation'; text: string };

@Component({
  selector: 'app-lc-timeline-filtered',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="tl-wrap">

      <!-- Summary stat strip -->
      <div class="tl-summary" *ngIf="summaryText">
        <span class="lc-id">{{ lcId }}</span>
        <ng-container *ngIf="lcId"> · </ng-container>
        {{ summaryTail }}
      </div>

      <!-- Filter pills (unchanged) -->
      <div class="tl-filters">
        <button class="tl-filter" [class.active]="activeFilter === 'all'" (click)="setFilter('all')">
          All ({{ data.length }})
        </button>
        <button *ngIf="hasType('approval')"
          class="tl-filter" [class.active]="activeFilter === 'approval'" (click)="setFilter('approval')">
          Approval ({{ countType('approval') }})
        </button>
        <button *ngIf="hasType('amendment')"
          class="tl-filter" [class.active]="activeFilter === 'amendment'" (click)="setFilter('amendment')">
          Amendment ({{ countType('amendment') }})
        </button>
        <button *ngIf="hasType('invoice')"
          class="tl-filter" [class.active]="activeFilter === 'invoice'" (click)="setFilter('invoice')">
          Invoice ({{ countType('invoice') }})
        </button>
      </div>

      <!-- Timeline -->
      <div class="tl-events">
        <ng-container *ngFor="let item of displayItems">

          <!-- Date separator chip -->
          <div class="tl-separator" *ngIf="item.type === 'separator'">
            <span class="sep-line"></span>
            <span class="sep-label">{{ asSeparator(item).dateLabel }}</span>
            <span class="sep-line"></span>
          </div>

          <!-- Elapsed time annotation pill -->
          <div class="tl-annotation" *ngIf="item.type === 'annotation'">
            {{ asAnnotation(item).text }}
          </div>

          <!-- Normal event -->
          <div class="tl-event" *ngIf="item.type === 'event'" [class.last]="asEvent(item).isLast">
            <div class="tl-left">
              <div class="tl-dot" [ngClass]="'dot-' + logType(asEvent(item).event)">
                <svg viewBox="0 0 20 20" width="20" height="20">
                  <!-- Approval: checkmark -->
                  <path *ngIf="logType(asEvent(item).event) === 'approval'"
                    d="M5 10 L8.5 13.5 L15 7"
                    fill="none" stroke="white" stroke-width="1.8"
                    stroke-linecap="round" stroke-linejoin="round"/>
                  <!-- Amendment: pencil -->
                  <path *ngIf="logType(asEvent(item).event) === 'amendment'"
                    d="M6 14 L12 8 M10.5 6.5 L13.5 9.5"
                    fill="none" stroke="white" stroke-width="1.5"
                    stroke-linecap="round"/>
                  <!-- Invoice: document -->
                  <path *ngIf="logType(asEvent(item).event) === 'invoice'"
                    d="M7 6 h4 l3 3 v8 H7 Z M11 6 v3 h3 M9 11 h5 M9 13 h5"
                    fill="none" stroke="white" stroke-width="1"
                    stroke-linecap="round"/>
                  <!-- Other: centre dot -->
                  <circle *ngIf="logType(asEvent(item).event) === 'other'"
                    cx="10" cy="10" r="3" fill="white"/>
                </svg>
              </div>
              <div class="tl-line" *ngIf="!asEvent(item).isLast"></div>
            </div>

            <div class="tl-content">
              <div class="tl-action-row">
                <span class="tl-action">{{ asEvent(item).event['Action'] }}</span>
                <span class="tl-type-badge" [ngClass]="'badge-' + logType(asEvent(item).event)">
                  {{ asEvent(item).event['LogType'] ?? 'event' }}
                </span>
              </div>
              <div class="tl-meta">
                <div class="tl-avatar"
                  [style.background]="getAvatarColor(str(asEvent(item).event['ActionedBy'])).bg"
                  [style.color]="getAvatarColor(str(asEvent(item).event['ActionedBy'])).text">
                  {{ getInitials(str(asEvent(item).event['ActionedBy'])) }}
                </div>
                <span class="tl-by">{{ asEvent(item).event['ActionedBy'] ?? '—' }}</span>
                <span class="tl-dot-sep">·</span>
                <span class="tl-on">{{ formatDateTime(asEvent(item).event['ActionedOn']) }}</span>
              </div>
              <div class="tl-comment" *ngIf="asEvent(item).event['Comment']">
                {{ asEvent(item).event['Comment'] }}
              </div>
            </div>
          </div>

        </ng-container>

        <!-- Total lifecycle annotation at end -->
        <div class="tl-lifecycle" *ngIf="lifecycleDuration">
          Total lifecycle: {{ lifecycleDuration }}
        </div>
      </div>

    </div>
  `,
  styles: [`
    .tl-wrap { display: flex; flex-direction: column; gap: 10px; }

    /* Summary strip */
    .tl-summary {
      font-size: 12px; color: var(--color-text-secondary, #6b7280);
      padding-bottom: 8px;
      border-bottom: 0.5px solid var(--color-border-tertiary, #e5e7eb);
      margin-bottom: 4px;
    }
    .tl-summary .lc-id { font-weight: 500; color: var(--color-text-primary, #111827); }

    /* Filter pills */
    .tl-filters { display: flex; gap: 6px; flex-wrap: wrap; }
    .tl-filter {
      background: none; border: 0.5px solid var(--color-border-secondary, #d1d5db);
      padding: 4px 12px; border-radius: 20px; font-size: 12px; cursor: pointer;
      color: var(--color-text-secondary, #6b7280); transition: all 0.15s;
    }
    .tl-filter.active {
      background: var(--color-background-secondary, #f8fafc);
      color: var(--color-text-primary, #111827);
      border-color: var(--color-border-primary, #9ca3af);
    }

    /* Events container */
    .tl-events { display: flex; flex-direction: column; }

    /* Date separator */
    .tl-separator { display: flex; align-items: center; gap: 8px; padding: 6px 0; }
    .sep-line { flex: 1; height: 0.5px; background: var(--color-border-tertiary, #e5e7eb); }
    .sep-label { font-size: 10px; font-weight: 500; color: var(--color-text-secondary, #6b7280); white-space: nowrap; }

    /* Elapsed annotation */
    .tl-annotation {
      font-size: 10px; color: var(--color-text-secondary, #6b7280);
      background: var(--color-background-secondary, #f8fafc);
      border-radius: 10px; padding: 2px 10px;
      display: inline-block; margin: 2px 0 2px 30px;
    }
    .tl-lifecycle {
      font-size: 11px; color: var(--color-text-secondary, #6b7280);
      padding: 8px 0 0 30px; font-style: italic;
    }

    /* Event row */
    .tl-event { display: grid; grid-template-columns: 30px 1fr; gap: 8px; }
    .tl-left { display: flex; flex-direction: column; align-items: center; }

    /* Dot with icon */
    .tl-dot {
      width: 20px; height: 20px; border-radius: 50%;
      display: flex; align-items: center; justify-content: center;
      flex-shrink: 0; margin-top: 2px; z-index: 1;
    }
    .dot-approval  { background: #639922; }
    .dot-amendment { background: #EF9F27; }
    .dot-invoice   { background: #185FA5; }
    .dot-other     { background: #888780; }

    .tl-line {
      width: 2px; flex: 1; background: var(--color-border-secondary, #d1d5db);
      margin: 2px 0 0; min-height: 16px;
    }

    /* Content area */
    .tl-content {
      padding: 4px 8px 12px;
      display: flex; flex-direction: column; gap: 3px;
      border-radius: 6px; transition: background 0.1s;
    }
    .tl-content:hover { background: var(--color-background-secondary, #f8fafc); }

    .tl-action-row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .tl-action { font-size: 13px; font-weight: 500; color: var(--color-text-primary, #111827); }

    .tl-type-badge { font-size: 10px; padding: 1px 7px; border-radius: 8px; }
    .badge-approval  { background: #EAF3DE; color: #3B6D11; }
    .badge-amendment { background: #FAEEDA; color: #854F0B; }
    .badge-invoice   { background: #E6F1FB; color: #185FA5; }
    .badge-other     { background: var(--color-background-secondary, #f8fafc); color: var(--color-text-secondary, #6b7280); }

    .tl-meta { display: flex; align-items: center; gap: 6px; font-size: 12px; }
    .tl-avatar {
      width: 20px; height: 20px; border-radius: 50%;
      display: flex; align-items: center; justify-content: center;
      font-size: 8px; font-weight: 600; flex-shrink: 0;
    }
    .tl-by { color: var(--color-text-secondary, #6b7280); }
    .tl-dot-sep { color: var(--color-text-tertiary, #9ca3af); }
    .tl-on { color: var(--color-text-secondary, #6b7280); font-size: 11px; }

    /* Comment quote block */
    .tl-comment {
      border-left: 2px solid var(--color-border-secondary, #d1d5db);
      padding: 3px 10px; font-size: 11px;
      color: var(--color-text-secondary, #6b7280);
      font-style: italic;
      background: var(--color-background-secondary, #f8fafc);
      border-radius: 0 4px 4px 0; margin-top: 2px;
    }
  `]
})
export class LcTimelineFilteredComponent implements OnInit {
  @Input() data: Record<string, unknown>[] = [];

  activeFilter = 'all';
  displayItems: DisplayItem[] = [];

  lcId = '';
  summaryText = '';
  summaryTail = '';
  lifecycleDuration = '';

  ngOnInit() {
    this.buildSummary();
    this.setFilter('all');
  }

  buildSummary() {
    if (!this.data.length) return;
    const people = new Set(this.data.map(e => e['ActionedBy'])).size;
    const first = new Date(String(this.data[0]['ActionedOn']));
    const last  = new Date(String(this.data[this.data.length - 1]['ActionedOn']));
    const days  = Math.round((last.getTime() - first.getTime()) / 86400000);
    const months = Math.floor(days / 30);
    this.lifecycleDuration = months > 0
      ? `${months} month${months > 1 ? 's' : ''} ${days % 30} days`
      : `${days} days`;
    this.lcId = String(this.data[0]?.['LcNumber'] ?? this.data[0]?.['RequestId'] ?? '');
    const tail = [
      `${this.data.length} events`,
      `${people} ${people === 1 ? 'person' : 'people'}`,
      `${this.formatDate(this.data[0]['ActionedOn'])} – ${this.formatDate(last.toISOString())}`,
      this.lifecycleDuration,
    ].join(' · ');
    this.summaryTail = tail;
    this.summaryText = [this.lcId, tail].filter(Boolean).join(' · ');
  }

  setFilter(f: string): void {
    this.activeFilter = f;
    const filtered = f === 'all'
      ? this.data
      : this.data.filter(e => this.logType(e) === f);
    this.displayItems = this.buildDisplayItems(filtered);
  }

  buildDisplayItems(events: Record<string, unknown>[]): DisplayItem[] {
    const items: DisplayItem[] = [];
    let lastDateStr = '';

    const submitIdx  = events.findIndex(e =>
      String(e['Action'] ?? '').toLowerCase().includes('submitted for approval'));
    const approveIdx = events.findIndex((e, i) =>
      i > submitIdx &&
      String(e['Action'] ?? '').toLowerCase().includes('approved') &&
      !String(e['Action'] ?? '').toLowerCase().includes('submitted'));

    events.forEach((ev, i) => {
      const evDate  = new Date(String(ev['ActionedOn']));
      const dateStr = evDate.toDateString();

      if (dateStr !== lastDateStr) {
        if (lastDateStr !== '') {
          items.push({ type: 'separator', dateLabel: this.formatDate(ev['ActionedOn']) });
        }
        lastDateStr = dateStr;
      }

      if (i === approveIdx && submitIdx >= 0) {
        const submitTime  = new Date(String(events[submitIdx]['ActionedOn'])).getTime();
        const approveTime = evDate.getTime();
        const hrs = Math.round((approveTime - submitTime) / 3_600_000);
        const label = hrs < 24
          ? `Approved in ${hrs} hours`
          : `Approved in ${Math.round(hrs / 24)} days`;
        items.push({ type: 'annotation', text: label });
      }

      items.push({ type: 'event', event: ev, isLast: i === events.length - 1 });
    });

    return items;
  }

  /* Type-narrowing helpers for the template */
  asEvent(item: DisplayItem)     { return item as Extract<DisplayItem, { type: 'event' }>; }
  asSeparator(item: DisplayItem) { return item as Extract<DisplayItem, { type: 'separator' }>; }
  asAnnotation(item: DisplayItem){ return item as Extract<DisplayItem, { type: 'annotation' }>; }

  logType(ev: Record<string, unknown>): string {
    return String(ev['LogType'] ?? 'other').toLowerCase();
  }

  str(v: unknown): string { return String(v ?? ''); }

  getAvatarColor(name: string): { bg: string; text: string } {
    const colors = [
      { bg: '#E6F1FB', text: '#0C447C' },
      { bg: '#EAF3DE', text: '#27500A' },
      { bg: '#EEEDFE', text: '#26215C' },
      { bg: '#FAEEDA', text: '#412402' },
      { bg: '#FAECE7', text: '#4A1B0C' },
      { bg: '#E1F5EE', text: '#04342C' },
    ];
    const idx = name.split('').reduce((s, c) => s + c.charCodeAt(0), 0) % colors.length;
    return colors[idx];
  }

  getInitials(name: string): string {
    const parts = name.trim().split(/\s+/).filter(Boolean);
    if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
    return name.slice(0, 2).toUpperCase();
  }

  hasType(t: string): boolean  { return this.data.some(e => this.logType(e) === t); }
  countType(t: string): number { return this.data.filter(e => this.logType(e) === t).length; }

  formatDateTime(value: unknown): string {
    if (!value) return '—';
    const d = new Date(String(value));
    if (isNaN(d.getTime())) return '—';
    return d.toLocaleString('en-GB', {
      day: '2-digit', month: 'short', year: 'numeric',
      hour: '2-digit', minute: '2-digit',
    });
  }

  formatDate(value: unknown): string {
    if (!value) return '—';
    const d = new Date(String(value));
    if (isNaN(d.getTime())) return '—';
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  }
}
