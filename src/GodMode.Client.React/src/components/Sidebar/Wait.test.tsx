// @vitest-environment jsdom
/**
 * WAIT is the server's to say (#441): a session waits on the user when its status does (WaitingInput, a
 * question it was stopped on), never because a line of its live output ended in '?'. A turn narrates
 * questions to itself and goes on; the server decides at the turn's end, from its final text. Drives the
 * real store through a fake hub, the left list and a tile on a session that is not selected.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectState, ProjectStatus } from '../../signalr/types';
import { parseClaudeMessage } from '../../signalr/parseMessage';
import { FakeHub, connectServers, project, root, status } from '../../test/fakeHub';
import { render, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { Sidebar } from './Sidebar';
import { ProjectTile } from '../Tiles/ProjectTile';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const narration = parseClaudeMessage(JSON.stringify({
  type: 'assistant', message: { content: [{ type: 'text', text: 'Builds clean. Now the tests: how do existing session tests use a mic?' }] },
}));

const asking = (state: ProjectState, question: string | null): ProjectStatus => ({ ...status('p1', state), Name: 'worker', UpdatedAt: new Date().toISOString(), CurrentQuestion: question });

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered | undefined;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  localStorage.clear();
  hub = new FakeHub([
    project('p1', 'worker', 'Running', new Date().toISOString()),
    project('p2', 'other', 'Idle', new Date().toISOString()),
  ], [root]);
  await connectServers({ A: hub });
  // The user is on another session: nothing clears p1's state for being selected
  useAppStore.getState().selectProject('A', 'p2');
});

afterEach(() => { view?.unmount(); view = undefined; });

/** The worker's state in the left list, as its dot names it (#437). */
const badge = () => [...view!.container.querySelectorAll('.project-item')]
  .find(el => el.querySelector('.project-name')?.textContent === 'worker')?.querySelector('.project-state-dot')?.getAttribute('aria-label');

/** The worker's tile, as the tile view renders it from the store. */
function Tile() {
  const p = useAppStore(s => s.getConnection('A')!.projects.find(x => x.Id === 'p1')!);
  return <ProjectTile project={p} messages={[]} isLoading={false} isSelected={false} onSelect={() => {}} />;
}
const tileWaits = () => view!.container.querySelector('.tile-waiting-badge') !== null;

const push = (s: ProjectStatus) => act(async () => hub.callbacks.onStatusChanged?.('p1', s));

describe('a session that is not selected', () => {
  it('is not WAIT for a line ending in ? in the middle of its turn, nor once the turn ends Idle with no question', async () => {
    view = await render(<><Sidebar /><Tile /></>);
    await act(async () => hub.callbacks.onOutputReceived?.('p1', { offset: 10, message: narration }));
    expect(badge()).toBe('Running');
    expect(tileWaits()).toBe(false);

    await push(asking('Idle', null));
    expect(badge()).toBe('Idle');
    expect(tileWaits()).toBe(false);
  });

  it('is WAIT when its status is WaitingInput with a question, and not once it runs again', async () => {
    view = await render(<><Sidebar /><Tile /></>);
    await push(asking('WaitingInput', 'Shall I open the PR?'));
    expect(badge()).toBe('Waiting on you');
    expect(tileWaits()).toBe(true);

    await push(asking('Running', null));
    expect(badge()).toBe('Running');
    expect(tileWaits()).toBe(false);
  });

  it('is WAIT when it was stopped on a question it still asks, and STOP when stopped on none', async () => {
    view = await render(<><Sidebar /><Tile /></>);
    await push(asking('Stopped', 'Shall I open the PR?'));
    expect(badge()).toBe('Waiting on you');
    expect(tileWaits()).toBe(true);

    await push(asking('Stopped', null));
    expect(badge()).toBe('Stopped');
    expect(tileWaits()).toBe(false);
  });
});
