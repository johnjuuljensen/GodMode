// @vitest-environment jsdom
/**
 * Every replay belongs to one subscription (#239). The fake server answers each SubscribeProject only
 * when the test says, so an answer can land after the client has sent a newer subscription for the
 * same transcript or tile. A batch or complete that answers any subscription but the current one must
 * change nothing. Drives the real store, and the tile grid, through fake hubs.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeHub, project, root, connectServers, flush, line, texts } from '../test/fakeHub';
import { render, type Rendered } from '../test/render';
import { TileGrid } from '../components/Tiles/TileGrid';
import { useAppStore } from './index';
import { projectKey } from './projectKey';

vi.mock('../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
  isMaui: false,
  clearApiKey: () => {},
}));

const at = (...offsets: number[]) => offsets.map(o => `at ${o}`);
const key = projectKey('A', 'p1');
const store = () => useAppStore.getState();

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered | undefined;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hub = new FakeHub([
    project('p1', 'first', 'Running', '2026-09-24T10:00:00Z'),
    project('p2', 'second', 'Running', '2026-09-24T09:00:00Z'),
  ], [root]);
  await connectServers({ A: hub });
});

afterEach(() => { view?.unmount(); view = undefined; });

describe('a tile', () => {
  // The blank tile: on a wake the catch-up resubscribes the tile from its offset, and the tile grid,
  // re-rendered by the new list, tails it again. The resume's complete ended the new tail's loading,
  // and the tail's batch was then refused
  it.each([
    ['in the order the server got them', false],
    ['newest first', true],
  ])('resumed on a wake and tailed again shows its lines, with the answers landing %s', async (_order, newestFirst) => {
    store().setTileView(true);
    await store().subscribeTail('A', 'p1', 2);
    hub.replaysOf('p1')[0].answer(5, [10, 20]);

    await hub.drop();
    await hub.reconnect();
    await store().unsubscribeTail('A', 'p1');
    store().clearTileMessages();
    await store().subscribeTail('A', 'p1', 2);
    expect(hub.subscriptions).toEqual([
      { projectId: 'p1', fromOffset: -2 }, { projectId: 'p1', fromOffset: 20 }, { projectId: 'p1', fromOffset: -2 },
    ]);

    const [, resumed, tailed] = hub.replaysOf('p1');
    const answers = [() => resumed.answer(20, []), () => tailed.answer(5, [10, 20])];
    for (const answer of newestFirst ? answers.reverse() : answers) answer();

    expect(texts(store().tileMessages[key])).toEqual(at(10, 20));
    expect(store().tileLoading[key]).toBe(false);
  });
});

describe('a transcript', () => {
  // The reset: a second subscription from 0 (the project opened during the catch-up, or StrictMode)
  // emptied the transcript on its first batch, then rebuilt it batch by batch
  it('subscribed twice from 0 never shrinks while both replays land', async () => {
    store().selectProject('A', 'p1');
    await store().subscribeOutput('A', 'p1');
    await store().subscribeOutput('A', 'p1');
    expect(hub.subscriptions).toEqual([{ projectId: 'p1', fromOffset: 0 }, { projectId: 'p1', fromOffset: 0 }]);

    const lengths: number[] = [];
    const stop = useAppStore.subscribe(s => lengths.push(s.outputMessages.length));
    for (const replay of hub.replaysOf('p1')) {
      replay.batch(0, [10, 20]);
      replay.batch(20, [30, 40]);
      replay.batch(40, [50, 60]);
      replay.complete(60);
    }
    stop();

    expect(lengths).toEqual([...lengths].sort((a, b) => a - b));
    expect(texts(store().outputMessages)).toEqual(at(10, 20, 30, 40, 50, 60));
    expect(store().transcripts[key]).toMatchObject({ offset: 60, phase: 'live' });
  });

  // The lost range: the complete of a subscription whose batches were refused switched the resubscribed
  // transcript to live, a live line moved its offset on, and the new replay dropped everything before it
  it('keeps the range a new replay brings when an older subscription completes first', async () => {
    store().selectProject('A', 'p1');
    await store().subscribeOutput('A', 'p1');
    hub.replaysOf('p1')[0].answer(0, [10, 20]);
    await store().unsubscribeOutput('A', 'p1');

    // Opened again, closed during the replay, and opened again: the server answers in the order it got them
    await store().subscribeOutput('A', 'p1');
    const older = hub.replaysOf('p1')[1];
    older.batch(20, [30, 40]);
    await store().unsubscribeOutput('A', 'p1');
    older.batch(40, [50, 60]);
    await store().subscribeOutput('A', 'p1');
    expect(hub.subscriptions.map(s => s.fromOffset)).toEqual([0, 20, 40]);
    older.complete(60);
    // Broadcast to the connection between the older replay's end and the server seeing the close
    hub.callbacks.onOutputReceived?.('p1', line(70));
    hub.replaysOf('p1')[2].answer(40, [50, 60, 70]);

    expect(texts(store().outputMessages)).toEqual(at(10, 20, 30, 40, 50, 60, 70));
    expect(store().transcripts[key]).toMatchObject({ offset: 70, phase: 'live' });
  });
});

describe('the tile grid', () => {
  it('over a wake that lists a new project, subscribes only the new tile, and every tile keeps its lines', async () => {
    store().setTileView(true);
    view = await render(<TileGrid />);
    const [p1, p2] = [hub.replaysOf('p1')[0], hub.replaysOf('p2')[0]];
    await act(async () => { p1.answer(5, [10, 20]); p2.answer(50, [60]); });
    hub.resetCalls();

    await act(() => hub.drop());
    hub.projects = [...hub.projects, project('p3', 'third', 'Running', '2026-09-24T11:00:00Z')];
    await act(() => hub.reconnect());
    await act(flush);

    // The tile added may be subscribed before or after the catch-up resumes the others
    expect([...hub.subscriptions].sort((a, b) => a.projectId.localeCompare(b.projectId))).toEqual([
      { projectId: 'p1', fromOffset: 20 }, { projectId: 'p2', fromOffset: 60 }, { projectId: 'p3', fromOffset: -2 },
    ]);
    await act(async () => {
      for (const replay of hub.replays.slice(2)) replay.answer(replay.fromOffset < 0 ? 70 : replay.fromOffset, replay.projectId === 'p3' ? [80] : []);
    });
    expect(texts(store().tileMessages[key])).toEqual(at(10, 20));
    expect(texts(store().tileMessages[projectKey('A', 'p2')])).toEqual(at(60));
    expect(texts(store().tileMessages[projectKey('A', 'p3')])).toEqual(at(80));
  });
});
