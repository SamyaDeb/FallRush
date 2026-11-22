// localStorage access, injectable for tests; tolerant of private mode (throws) by falling back to memory.
export interface KV { getItem(k: string): string | null; setItem(k: string, v: string): void; removeItem(k: string): void }

export function memoryKV(): KV {
  const m = new Map<string, string>();
  return { getItem: (k) => (m.has(k) ? m.get(k)! : null), setItem: (k, v) => void m.set(k, v), removeItem: (k) => void m.delete(k) };
}

export function browserKV(): { kv: KV; persistent: boolean } {
  try {
    const ls = (globalThis as { localStorage?: KV }).localStorage;
    if (ls) {
      const probe = '__fallrush_probe__';
      ls.setItem(probe, '1');
      ls.removeItem(probe);
      return { kv: ls, persistent: true };
    }
  } catch { /* blocked storage */ }
  return { kv: memoryKV(), persistent: false };
}
