// @vitest-environment jsdom
/**
 * A /clear starts a project's output over (#31): the server pushes OutputRestarted with the new generation, and the
 * live lines that follow are the new file's, from its start. A transcript or tile that follows it live drops what it
 * held and takes them; one still replaying is left to its replay. Drives the real store through a fake hub.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeHub, project, root, connectServers, line, texts } from '../test/fakeHub';
import { useAppStore } from './index';
import { projectKey } from './projectKey';

vi.mock('../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const at = (...offsets: number[]) => offsets.map(o => `at ${o}`);
const key = projectKey('A', 'p1');
const store = () => useAppStore.getState();

const initialState = useAppStore.getState();
let hub: FakeHub;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hub = new FakeHub([project('p1', 'first', 'Running', '2026-10-02T10:00:00Z')], [root]);
  await connectServers({ A: hub });
});

describe('an output restarted by /clear', () => {
  it('empties the open transcript, which takes the new generation from its start', async () => {
    store().selectProject('A', 'p1');
    await store().subscribeOutput('A', 'p1');
    hub.replaysOf('p1')[0].answer(0, [10, 20, 30]);

    hub.restartOutput('p1', 'g2');
    expect(store().outputMessages).toEqual([]);
    hub.callbacks.onOutputReceived?.('p1', line(5));
    hub.callbacks.onOutputReceived?.('p1', line(12));

    expect(texts(store().outputMessages)).toEqual(at(5, 12));
    expect(store().transcripts[key]).toMatchObject({ offset: 12, generation: 'g2', phase: 'live' });
  });

  it('leaves a transcript still replaying to its replay, which brings the new generation from 0', async () => {
    store().selectProject('A', 'p1');
    await store().subscribeOutput('A', 'p1');
    const replay = hub.replaysOf('p1')[0];
    replay.batch(0, [10, 20]);

    hub.restartOutput('p1', 'g2');
    expect(texts(store().outputMessages)).toEqual(at(10, 20));
    replay.batch(0, [5]);
    replay.complete(5);

    expect(texts(store().outputMessages)).toEqual(at(5));
    expect(store().transcripts[key]).toMatchObject({ offset: 5, generation: 'g2', phase: 'live' });
  });

  it("empties a live tile's lines, which takes the new generation's", async () => {
    store().setTileView(true);
    await store().subscribeTail('A', 'p1', 2);
    hub.replaysOf('p1')[0].answer(5, [10, 20]);

    hub.restartOutput('p1', 'g2');
    hub.callbacks.onOutputReceived?.('p1', line(7));

    expect(texts(store().tileMessages[key])).toEqual(at(7));
    expect(store().tiles[key]).toMatchObject({ offset: 7, generation: 'g2' });
  });
});
