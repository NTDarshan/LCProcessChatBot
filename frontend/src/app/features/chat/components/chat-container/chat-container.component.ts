import {
  Component, ChangeDetectionStrategy, inject, computed, OnDestroy
} from '@angular/core';
import { MessageStore } from '../../services/message.store';
import { ChatMessageComponent } from '../chat-message/chat-message.component';
import { ChatInputComponent } from '../chat-input/chat-input.component';
import { TypingIndicatorComponent } from '../typing-indicator/typing-indicator.component';
import { AuthService } from '../../../../auth/auth.service';

const SUGGESTED_QUERIES = [
  { icon: '📄', text: 'Show my issued LCs',         query: 'Show my issued LCs'         },
  { icon: '📄', text: 'View LC status overview',    query: 'View LC status overview'    },
  { icon: '⏰', text: 'Review delayed LC requests', query: 'Review delayed LC requests' },
  { icon: '🏦', text: 'Analyze top issuing banks',  query: 'Analyze top issuing banks'  },
];

@Component({
  selector: 'app-chat-container',
  standalone: true,
  imports: [ChatMessageComponent, ChatInputComponent, TypingIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chat-container.component.html',
  styleUrl:    './chat-container.component.scss',
})
export class ChatContainerComponent implements OnDestroy {
  protected readonly store = inject(MessageStore);
  private  readonly auth  = inject(AuthService);

  protected readonly suggestedQueries = SUGGESTED_QUERIES;

  protected readonly connectionLabel = computed(() => {
    const status = this.store.connectionStatus();
    if (status === 'connected') return 'Connected';
    if (status === 'connecting') return 'Reconnecting';
    return 'Disconnected';
  });

  protected readonly initials = computed(() => {
    const name = this.auth.userName();
    if (!name) return 'U';
    return name.split(' ').map(w => w[0]).join('').slice(0, 2).toUpperCase();
  });

  // ── ngOnDestroy – cooperative cancellation on component destroy ───────────
  // Fires when the user navigates away from the chat view.
  // Stops any in-flight generation to prevent orphaned background tasks and
  // stale chunks arriving after the component has been destroyed.
  ngOnDestroy(): void {
    this.store.stopGeneration();
  }
}
