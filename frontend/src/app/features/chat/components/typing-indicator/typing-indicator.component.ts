import { Component, ChangeDetectionStrategy, input, signal } from '@angular/core';
import { KeyValuePipe } from '@angular/common';
import { ProcessingStageUpdate } from '../../../../shared/models/chat.models';


@Component({
  selector: 'app-typing-indicator',
  standalone: true,
  imports: [KeyValuePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './typing-indicator.component.html',
  styleUrl:    './typing-indicator.component.scss',
})
export class TypingIndicatorComponent {
  readonly stages = input<ProcessingStageUpdate[]>([]);
  readonly currentStage = input<string>('Preparing...');
  readonly liveLabel = input<string>('Thinking...');

  readonly isCollapsed = signal<boolean>(true);

  toggleCollapse(): void {
    this.isCollapsed.update((v) => !v);
  }

  trackByStage(_index: number, stage: ProcessingStageUpdate): string {
    return stage.stageKey;
  }
}
