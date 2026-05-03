import {
  Component, signal, inject, ChangeDetectionStrategy,
  ElementRef, viewChild
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MessageStore } from '../../services/message.store';

@Component({
  selector: 'app-chat-input',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chat-input.component.html',
  styleUrl:    './chat-input.component.scss',
})
export class ChatInputComponent {
  protected readonly store     = inject(MessageStore);
  protected readonly inputText = signal('');
  protected readonly isTyping  = this.store.isTyping;
  protected readonly isStreaming = this.store.isStreaming;

  private readonly textAreaRef = viewChild<ElementRef<HTMLTextAreaElement>>('textArea');

  onEnter(e: Event): void {
    const ke = e as KeyboardEvent;
    if (!ke.shiftKey) {
      ke.preventDefault();
      this.send();
    }
  }

  send(): void {
    const text = this.inputText().trim();
    if (!text || this.isTyping() || this.isStreaming()) return;
    this.inputText.set('');
    this.resetTextarea();
    this.store.sendMessage(text);
  }

  stop(): void {
    // TODO: cancel streaming when SignalR is wired up
    this.store.isTyping.set(false);
  }

  autoResize(): void {
    const el = this.textAreaRef()?.nativeElement;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 160) + 'px';
  }

  private resetTextarea(): void {
    const el = this.textAreaRef()?.nativeElement;
    if (el) el.style.height = 'auto';
  }
}
