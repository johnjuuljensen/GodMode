/** A Map-backed localStorage for Node: the store reads it at import. Cleared before each test. */
import { beforeEach } from 'vitest';

class MemoryStorage implements Storage {
  private items = new Map<string, string>();
  get length() { return this.items.size; }
  clear() { this.items.clear(); }
  getItem(key: string) { return this.items.get(key) ?? null; }
  key(index: number) { return [...this.items.keys()][index] ?? null; }
  removeItem(key: string) { this.items.delete(key); }
  setItem(key: string, value: string) { this.items.set(key, String(value)); }
}

globalThis.localStorage = new MemoryStorage();
beforeEach(() => localStorage.clear());
