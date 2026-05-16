import { Component, Input, Output, EventEmitter } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Clarification } from '../../../../shared/models/chat.models';

@Component({
  selector: 'app-lc-clarification',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="clarification-wrap">
      <div class="clarif-header">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#92400E" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <path d="M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z"/>
          <line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>
        </svg>
        <span>I couldn't find {{ entityTypeLabel }} matching "{{ clarification.unrecognisedValue }}"</span>
      </div>

      <div class="clarif-subtitle">Available {{ entityTypeLabelPlural }} in your portfolio:</div>

      <div class="options-grid">
        @for (opt of clarification.availableOptions; track opt) {
          <button class="option-card" (click)="selectOption(opt)">{{ opt }}</button>
        }
      </div>

      <div class="clarif-footer">Click any option to search with that value</div>
    </div>
  `,
  styles: [`
    .clarification-wrap {
      display: flex; flex-direction: column; gap: 10px;
      background: #FFFBEB; border: 1px solid #F59E0B;
      border-radius: 12px; padding: 14px; margin-top: 8px;
    }
    .clarif-header {
      display: flex; align-items: center; gap: 8px;
      font-size: 13px; font-weight: 600; color: #92400E;
    }
    .clarif-subtitle { font-size: 11px; color: #B45309; }
    .options-grid { display: flex; flex-wrap: wrap; gap: 8px; }
    .option-card {
      padding: 7px 14px; border-radius: 8px;
      border: 1px solid #FCD34D; background: white;
      font-size: 12px; font-weight: 500; color: #92400E;
      cursor: pointer; transition: all 0.15s;
    }
    .option-card:hover {
      border-color: #6366f1; background: #F0F4FF; color: #4F46E5;
    }
    .clarif-footer { font-size: 11px; color: #9CA3AF; }
  `]
})
export class LcClarificationComponent {
  @Input() clarification!: Clarification;
  @Input() originalQuestion = '';
  @Output() optionSelected = new EventEmitter<string>();

  get entityTypeLabel(): string {
    switch (this.clarification.entityType) {
      case 'bank':     return 'bank';
      case 'customer': return 'customer';
      case 'lc_number': return 'LC number';
      default: return this.clarification.entityType;
    }
  }

  get entityTypeLabelPlural(): string {
    switch (this.clarification.entityType) {
      case 'bank':     return 'banks';
      case 'customer': return 'customers';
      case 'lc_number': return 'LC numbers';
      default: return this.clarification.entityType + 's';
    }
  }

  selectOption(option: string): void {
    const escaped = this.clarification.unrecognisedValue.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const question = this.clarification.questionTemplate.replace(
      new RegExp(escaped, 'gi'),
      option
    );
    this.optionSelected.emit(question);
  }
}
