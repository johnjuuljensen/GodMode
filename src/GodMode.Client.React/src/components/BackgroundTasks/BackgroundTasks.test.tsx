// @vitest-environment jsdom
/**
 * A session's background tasks (#432): an idle session whose claude still runs subagents, shells, monitors or workflows
 * shows as working in the background, not Idle. Its row's dot says so, a badge counts the tasks and lists them, each with
 * its last step, in its tooltip, and its header's button says Background and stops it. The status pushed carries them.
 */
import { act } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { BackgroundTask, ProjectState } from '../../signalr/types';
import { FakeHub, project, root, connectServers, status } from '../../test/fakeHub';
import { render, type Rendered } from '../../test/render';
import { useAppStore, projectKey } from '../../store';
import { ProjectItem } from '../Sidebar/ProjectItem';
import { ProjectView } from '../Project/ProjectView';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const tasks: BackgroundTask[] = [
  { Id: 'a1', Type: 'local_agent', Description: 'Review the diff', Step: 'Running the tests' },
  { Id: 'b1', Type: 'local_bash', Description: 'Watch CI' },
];
const listed = 'Working in the background (2 tasks)\n• Review the diff — Running the tests\n• Watch CI';

const initialState = useAppStore.getState();
let view: Rendered | undefined;

afterEach(() => {
  view?.unmount();
  view = undefined;
  useAppStore.setState(initialState, true);
});

const row = (state: ProjectState, background?: BackgroundTask[]) => {
  const p = { ...project('p1', 'a session', state, new Date().toISOString()), BackgroundTasks: background };
  return render(<ProjectItem item={{ key: projectKey('A', 'p1'), serverId: 'A', project: p, children: [] }} isSelected={false} onSelect={() => {}} />);
};
const dot = () => view!.container.querySelector<HTMLElement>('.project-item .project-state-dot')!;
const badge = () => view!.container.querySelector<HTMLElement>('.background-badge');

describe('a row', () => {
  it('of an idle session with background tasks is working in the background, and its badge lists them', async () => {
    view = await row('Idle', tasks);
    expect([...dot().classList]).toEqual(['project-state-dot', 'Background']);
    expect(dot().getAttribute('aria-label')).toBe('Working in the background (2 tasks)');
    expect(badge()!.textContent).toBe('2');
    expect(badge()!.title).toBe(listed);
  });

  it('of a running session keeps its state, with the badge beside it', async () => {
    view = await row('Running', tasks.slice(1));
    expect([...dot().classList]).toEqual(['project-state-dot', 'Running']);
    expect(badge()!.getAttribute('aria-label')).toBe('Working in the background (1 task)');
  });

  it('of an idle session with none is Idle, with no badge', async () => {
    view = await row('Idle', []);
    expect([...dot().classList]).toEqual(['project-state-dot', 'Idle']);
    expect(badge()).toBeNull();
  });
});

describe('the header', () => {
  const open = async (state: ProjectState) => {
    const hub = new FakeHub([{ ...project('p1', 'worker', state, '2026-09-24T12:00:00Z'), BackgroundTasks: tasks }], [root]);
    await connectServers({ A: hub });
    useAppStore.getState().selectProject('A', 'p1');
    view = await render(<ProjectView serverId="A" projectId="p1" />);
    return hub;
  };
  const button = () => view!.container.querySelector<HTMLButtonElement>('.project-status-btn')!;

  it('says Background with the count, lists the tasks, and stops it rather than resuming it', async () => {
    const hub = await open('Idle');
    expect(button().classList.contains('Background')).toBe(true);
    expect(button().querySelector('.project-status-label')!.textContent).toBe('Background · 2');
    expect(button().title).toBe(`${listed}\nClick to stop`);
    expect(button().querySelector('.project-status-action')!.textContent).toBe('Stop');

    // The list empties: Idle again, and Resume
    await act(async () => hub.callbacks.onStatusChanged?.('p1', { ...status('p1', 'Idle'), Name: 'worker', BackgroundTasks: null }));
    expect(button().querySelector('.project-status-label')!.textContent).toBe('Idle');
    expect(button().querySelector('.project-status-action')!.textContent).toBe('Resume');
  });

  it('of a running session keeps Running, with the badge by its name', async () => {
    await open('Running');
    expect(button().querySelector('.project-status-label')!.textContent).toBe('Running');
    expect(view!.container.querySelector<HTMLElement>('.project-header .background-badge')!.title).toBe(listed);
  });
});

it('a status pushed brings its tasks to the list', async () => {
  const hub = new FakeHub([project('p1', 'worker', 'Idle', '2026-09-24T12:00:00Z')], [root]);
  await connectServers({ A: hub });

  await act(async () => hub.callbacks.onStatusChanged?.('p1', { ...status('p1', 'Idle'), Name: 'worker', BackgroundTasks: tasks }));

  expect(useAppStore.getState().getConnection('A')!.projects[0].BackgroundTasks).toEqual(tasks);
});
