import { Component, ChangeDetectionStrategy, inject, signal } from '@angular/core';
import { SidebarComponent } from '../features/sidebar/sidebar.component';
import { ChatContainerComponent } from '../features/chat/components/chat-container/chat-container.component';
import { MessageStore } from '../features/chat/services/message.store';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [SidebarComponent, ChatContainerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  protected readonly store = inject(MessageStore);
  protected readonly sidebarCollapsed = signal(false);
  protected readonly activeNav = signal('assistant');
}
