import {
  Component, ChangeDetectionStrategy, inject, computed, signal, output, OnInit, OnDestroy
} from '@angular/core';
import { MessageStore } from '../chat/services/message.store';
import { AuthService } from '../../auth/auth.service';
import { LcApiService } from '../../core/services/lc-api.service';
import { ChatSession } from '../../shared/models/chat.models';

const NAV_ITEMS = [
  { id: 'assistant', label: 'Assistant', icon: 'chat' },
];

@Component({
  selector: 'app-sidebar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss',
})
export class SidebarComponent implements OnInit {
  protected readonly store = inject(MessageStore);
  protected readonly auth = inject(AuthService);
  protected readonly navItems = NAV_ITEMS;

  // Internal collapse state – emits to parent so dashboard can adjust layout
  protected readonly collapsed = signal(false);
  readonly collapsedChange = output<boolean>();

  protected readonly activeNav = signal('assistant');
  readonly navChange = output<string>();

  protected readonly initials = computed(() => {
    const name = this.auth.userName();
    if (!name) return 'U';
    return name.split(' ').map(w => w[0]).join('').slice(0, 2).toUpperCase();
  });

  // Load sessions from DB when the sidebar is initialised
  ngOnInit(): void {
    this.store.loadSessions();
  }

  toggleCollapse(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    this.collapsedChange.emit(next);
  }

  newChat(): void {
    this.store.newSession();
    this.setNav('assistant');
  }

  setNav(id: string): void {
    this.activeNav.set(id);
    this.navChange.emit(id);
  }

  // Clicks on a sidebar session: load its full history from the DB
  openSession(session: ChatSession): void {
    this.store.loadSession(session);
  }

  // Deletes a session with optimistic UI update
  deleteSession(event: MouseEvent, session: ChatSession): void {
    event.stopPropagation(); // prevent triggering openSession
    this.store.deleteSession(session.sessionId);
  }

  // Derive a display title: use stored title, fallback to session date
  sessionTitle(session: ChatSession): string {
    if (session.title) {
      return session.title.length > 35
        ? session.title.slice(0, 35) + '…'
        : session.title;
    }
    return `Session ${new Date(session.createdAt).toLocaleDateString()}`;
  }

  formatTime(date: Date): string {
    const now = new Date();
    const diff = now.getTime() - new Date(date).getTime();
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return 'Just now';
    if (mins < 60) return `${mins} min${mins === 1 ? '' : 's'} ago`;
    const hours = Math.floor(mins / 60);
    if (hours < 24) return `${hours} hr${hours === 1 ? '' : 's'} ago`;
    if (Math.floor(hours / 24) === 1) return 'Yesterday';
    return `${Math.floor(hours / 24)} days ago`;
  }
}
