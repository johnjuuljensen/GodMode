/**
 * The hub's reconnect (#171): a lost connection is retried for as long as it takes, at most 30 s
 * apart, and at once when asked. Runs GodModeHub over a fake SignalR connection on fake timers.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { IRetryPolicy } from '@microsoft/signalr';
import { GodModeHub, type ConnectionState } from './hub';

type Callback = (arg?: unknown) => void;

/**
 * A HubConnection as SignalR runs one: start() fails while offline. When the connection drops,
 * it reconnects by the policy given to withAutomaticReconnect (SignalR's own loop), else it closes.
 */
class FakeConnection {
  offline = false;
  /** When each start() was made, in fake time. */
  starts: number[] = [];
  private closed: Callback[] = [];
  private reconnecting: Callback[] = [];
  private reconnected: Callback[] = [];
  private retry: number[] | IRetryPolicy | undefined;
  constructor(retry: number[] | IRetryPolicy | undefined) { this.retry = retry; }

  on() {}
  onclose(cb: Callback) { this.closed.push(cb); }
  onreconnecting(cb: Callback) { this.reconnecting.push(cb); }
  onreconnected(cb: Callback) { this.reconnected.push(cb); }
  /** When set, start() waits until hung() is called, then fails: a request the network holds. */
  hang = false;
  hung: () => void = () => {};
  async start() {
    this.starts.push(Date.now());
    const hanging = this.hang;
    if (hanging) await new Promise<void>(resolve => { this.hung = resolve; });
    if (this.offline || hanging) throw new Error('offline');
  }
  async stop() { this.closed.forEach(cb => cb()); }

  drop() {
    this.offline = true;
    const error = new Error('connection lost');
    if (!this.retry) { this.closed.forEach(cb => cb(error)); return; }
    this.reconnecting.forEach(cb => cb(error));
    const retry = this.retry;
    const since = Date.now();
    let attempts = 0;
    const next = () => Array.isArray(retry)
      ? retry[attempts] ?? null
      : retry.nextRetryDelayInMilliseconds({ previousRetryCount: attempts, elapsedMilliseconds: Date.now() - since, retryReason: error });
    const schedule = () => {
      const delay = next();
      if (delay === null) { this.closed.forEach(cb => cb(error)); return; }
      setTimeout(async () => {
        attempts++;
        try { await this.start(); this.reconnected.forEach(cb => cb('new-id')); } catch { schedule(); }
      }, delay);
    };
    schedule();
  }
}

const connections: FakeConnection[] = [];

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    private retry: number[] | IRetryPolicy | undefined;
    withUrl() { return this; }
    withAutomaticReconnect(retry?: number[] | IRetryPolicy) { this.retry = retry ?? [0, 2000, 10000, 30000]; return this; }
    configureLogging() { return this; }
    build() {
      const c = new FakeConnection(this.retry);
      connections.push(c);
      return c;
    }
  }
  return { HubConnectionBuilder, LogLevel: { Warning: 3 } };
});

let hub: GodModeHub;
let conn: FakeConnection;
let states: ConnectionState[];

beforeEach(async () => {
  vi.useFakeTimers();
  connections.length = 0;
  hub = new GodModeHub();
  states = [];
  hub.setCallbacks({ onStateChanged: s => states.push(s) });
  await hub.connect('http://test/hub');
  conn = connections[0];
  conn.starts = [];
});

afterEach(() => vi.useRealTimers());

describe('a lost connection', () => {
  it('is still being retried after 10 minutes offline, and connects when the network is back', async () => {
    conn.drop();
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(hub.state).toBe('reconnecting');
    const attempts = conn.starts.length;
    conn.offline = false;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(conn.starts.length).toBe(attempts + 1);
    expect(hub.state).toBe('connected');
  });

  it('is retried at most 30 s apart', async () => {
    const dropped = Date.now();
    conn.drop();
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    const gaps = [dropped, ...conn.starts].slice(1).map((t, i) => t - [dropped, ...conn.starts][i]);
    expect(Math.max(...gaps)).toBe(30_000);
    expect(gaps[0]).toBe(0);
    // Not a schedule that ends: every 30 s to the last
    expect(gaps.length).toBeGreaterThan(20);
  });

  it('is retried at once by retryNow, while waiting between attempts', async () => {
    conn.drop();
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    const attempts = conn.starts.length;
    conn.offline = false;
    hub.retryNow();
    await vi.advanceTimersByTimeAsync(0);
    expect(conn.starts.length).toBe(attempts + 1);
    expect(hub.state).toBe('connected');
  });

  it('asked to retry during an attempt that hangs, retries at once when that attempt fails', async () => {
    conn.drop();
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    conn.hang = true;
    await vi.advanceTimersByTimeAsync(30_000);
    const attempts = conn.starts.length;
    hub.retryNow();
    await vi.advanceTimersByTimeAsync(0);
    expect(conn.starts.length).toBe(attempts);
    conn.hang = false;
    conn.offline = false;
    conn.hung();
    await vi.advanceTimersByTimeAsync(0);
    expect(conn.starts.length).toBe(attempts + 1);
    expect(hub.state).toBe('connected');
  });

  it('is no longer retried once disconnected on purpose', async () => {
    conn.drop();
    await vi.advanceTimersByTimeAsync(60_000);
    await hub.disconnect();
    const attempts = conn.starts.length;
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(conn.starts.length).toBe(attempts);
    expect(hub.state).toBe('disconnected');
  });

  it('reports reconnecting then connected, never disconnected on the way', async () => {
    states.length = 0;
    conn.drop();
    await vi.advanceTimersByTimeAsync(2 * 60_000);
    conn.offline = false;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(states[0]).toBe('reconnecting');
    expect(states[states.length - 1]).toBe('connected');
    expect(states).not.toContain('disconnected');
  });
});
