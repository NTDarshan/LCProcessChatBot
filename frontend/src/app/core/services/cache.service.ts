import { Injectable } from '@angular/core';
import { CachedResponse } from '../../shared/models/chat.models';

@Injectable({ providedIn: 'root' })
export class CacheService {
  private readonly cache = new Map<string, CachedResponse>();
  private readonly TTL_MS = 5 * 60 * 1000; // 5 minutes

  private normalizeKey(query: string): string {
    return query.trim().toLowerCase().replace(/\s+/g, ' ');
  }

  get(query: string): string | null {
    const key = this.normalizeKey(query);
    const entry = this.cache.get(key);
    if (!entry) return null;
    if (Date.now() - entry.cachedAt > this.TTL_MS) {
      this.cache.delete(key);
      return null;
    }
    return entry.response;
  }

  set(query: string, response: string): void {
    const key = this.normalizeKey(query);
    this.cache.set(key, { response, cachedAt: Date.now() });
  }

  invalidate(query?: string): void {
    if (query) {
      this.cache.delete(this.normalizeKey(query));
    } else {
      this.cache.clear();
    }
  }
}
