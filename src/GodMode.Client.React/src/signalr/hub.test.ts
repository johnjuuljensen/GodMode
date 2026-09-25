/**
 * The hub's reconnect (#171): a lost connection is retried for as long as it takes, at most 30 s
 * apart, and at once when asked. Runs GodModeHub over a fake SignalR connection on fake timers.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { IRetryPolicy } from '@microsoft/signalr';
import { GodModeHub, HUNG_ATTEMPT_MS, type ConnectionState } from './hub';

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
  /** As HubConnection.state: start() throws unless Disconnected, as SignalR's does. */
  state: 'Disconnected' | 'Connecting' | 'Connected' = 'Disconnected';
  /** Each start() made while another was in flight or connected: SignalR throws on it. */
  overlapping = 0;
  /** When set, start() waits until hung() is called, or stop(), then fails: a request the network holds. */
  hang = false;
  hung: () => void = () => {};
  async start() {
    this.starts.push(Date.now());
    if (this.state !== 'Disconnected') { this.overlapping++; throw new Error("Cannot start a HubConnection that is not in the 'Disconnected' state."); }
    this.state = 'Connecting';
    const hanging = this.hang;
    if (hanging) await new Promise<void>(resolve => { this.hung = resolve; });
    if (this.offline || hanging || this.state !== 'Connecting') { this.state = 'Disconnected'; throw new Error('offline'); }
    this.state = 'Connected';
  }
  /** When cleared, stop() leaves an attempt in flight hanging: a request the network holds past its abort. */
  stopAborts = true;
  /** As SignalR: stopping an attempt in flight makes it fail; stopping an open connection closes it. */
  async stop() {
    const was = this.state;
    this.state = 'Disconnected';
    if (was === 'Connecting') { if (this.stopAborts) this.hung(); }
    else if (was === 'Connected') this.closed.forEach(cb => cb());
  }

  drop() {
    this.offline = true;
    this.state = 'Disconnected';
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
/** Whether a connection built from here on starts offline: a server down when the page loads. */
let buildOffline = false;

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    private retry: number[] | IRetryPolicy | undefined;
    withUrl() { return this; }
    withAutomaticReconnect(retry?: number[] | IRetryPolicy) { this.retry = retry ?? [0, 2000, 10000, 30000]; return this; }
    configureLogging() { return this; }
    build() {
      const c = new FakeConnection(this.retry);
      c.offline = buildOffline;
      connections.push(c);
      return c;
    }
  }
  return { HubConnectionBuilder, LogLevel: { Warning: 3 }, HubConnectionState: { Disconnected: 'Disconnected' } };
});

let hub: GodModeHub;
let conn: FakeConnection;
let states: ConnectionState[];

beforeEach(async () => {
  vi.useFakeTimers();
  connections.length = 0;
  buildOffline = false;
  hub = new GodModeHub();
  states = [];
  hub.setCallbacks({ onStateChanged: s => states.push(s) });
  await hub.connect('http://test/hub');
  conn = connections[0];
  conn.starts = [];
});

afterEach(() => vi.useRealTimers());

/** Runs the clock until the connection's next start(). */
async function untilStart(c = conn) {
  const before = c.starts.length;
  while (c.starts.length === before) await vi.advanceTimersByTimeAsync(100);
}

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

  it('asked to retry during an attempt just made, retries at once when that attempt fails', async () => {
    conn.drop();
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    conn.hang = true;
    await untilStart();
    await vi.advanceTimersByTimeAsync(HUNG_ATTEMPT_MS - 1000);
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

  it('asked to retry during an attempt that hangs, stops it and retries at once (#221)', async () => {
    conn.drop();
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    conn.hang = true;
    await untilStart();
    await vi.advanceTimersByTimeAsync(HUNG_ATTEMPT_MS + 1000);
    const attempts = conn.starts.length;
    conn.hang = false;
    conn.offline = false;
    hub.retryNow();
    await vi.advanceTimersByTimeAsync(0);
    expect(conn.starts.length).toBe(attempts + 1);
    expect(hub.state).toBe('connected');
    expect(conn.overlapping).toBe(0);
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

describe('retries belong to their connection (#221)', () => {
  it('an old attempt that fails late leaves the new connection\'s attempt alone: a retryNow starts none beside it', async () => {
    conn.drop();
    await vi.advanceTimersByTimeAsync(60_000);
    // An attempt of the old connection hangs, and stopping it does not end it
    conn.hang = true;
    conn.stopAborts = false;
    await untilStart();
    const oldAttempts = conn.starts.length;
    await hub.connect('http://test/hub');
    const fresh = connections[1];
    // The new connection is lost too, and its first retry hangs
    fresh.hang = true;
    fresh.drop();
    await vi.advanceTimersByTimeAsync(0);
    expect(fresh.starts.length).toBe(2);
    // The old attempt fails now, then the page wakes
    conn.hung();
    await vi.advanceTimersByTimeAsync(0);
    hub.retryNow();
    await vi.advanceTimersByTimeAsync(0);
    expect(fresh.overlapping).toBe(0);
    fresh.hang = false;
    fresh.offline = false;
    fresh.hung();
    await vi.advanceTimersByTimeAsync(0);
    expect(hub.state).toBe('connected');
    expect(fresh.overlapping).toBe(0);
    expect(conn.starts.length).toBe(oldAttempts);
  });

  it('a retryNow during an attempt starts none beside it', async () => {
    conn.drop();
    await vi.advanceTimersByTimeAsync(60_000);
    conn.hang = true;
    await untilStart();
    for (let i = 0; i < 3; i++) hub.retryNow();
    await vi.advanceTimersByTimeAsync(0);
    conn.hung();
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(conn.overlapping).toBe(0);
  });

  it('keeps one chain of retries, however often it is asked: at most one start() in 30 s once they are 30 s apart', async () => {
    conn.drop();
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    for (let i = 0; i < 5; i++) { hub.retryNow(); await vi.advanceTimersByTimeAsync(10); }
    const before = conn.starts.length;
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    // Ten 30 s waits, not a second chain doubling them
    expect(conn.starts.length - before).toBe(10);
    expect(conn.overlapping).toBe(0);
  });
});

describe('a server that is down when the page loads (#221)', () => {
  it('is retried until it is up, and the first connect still reports it failed', async () => {
    await hub.disconnect();
    connections.length = 0;
    buildOffline = true;
    const down = new GodModeHub();
    const seen: ConnectionState[] = [];
    down.setCallbacks({ onStateChanged: s => seen.push(s) });
    const first = down.connect('http://test/down');
    const c = connections[0];
    await expect(first).rejects.toThrow('offline');
    expect(down.state).toBe('reconnecting');
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(c.starts.length).toBeGreaterThan(20);
    c.offline = false;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(down.state).toBe('connected');
    expect(seen).toEqual(['connecting', 'reconnecting', 'connected']);
  });

  it('is no longer retried once disconnected', async () => {
    connections.length = 0;
    buildOffline = true;
    const down = new GodModeHub();
    const first = down.connect('http://test/down');
    await first.catch(() => {});
    await down.disconnect();
    const attempts = connections[0].starts.length;
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(connections[0].starts.length).toBe(attempts);
  });
});
