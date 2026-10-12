// @vitest-environment jsdom
/**
 * How far an epic has got (#545): a session whose status script reports a progress shows `x/N` at its row's end in the
 * left list, folded or not, beside the count of what is under it, and in the project view's header with a thin bar.
 * A session with none shows nothing. A status push moves it. Renders the Shell and ProjectView on the real store.
 */
import { act, type ReactNode } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { ProjectRootInfo, ProjectSummary, WorkProgress } from '../../signalr/types';
import { FakeHub, connectServers, project, status } from '../../test/fakeHub';
import { render, click, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { Shell } from '../Shell';
import { ProjectView } from '../Project/ProjectView';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  subscribeAttentionLinks: () => () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));
// jsdom lays nothing out, so Virtuoso would render no rows: this one renders them all
vi.mock('react-virtuoso', () => ({
  Virtuoso: ({ data, itemContent, className }: { data: unknown[]; itemContent: (i: number, item: unknown) => ReactNode; className?: string }) => (
    <div className={className}>{data.map((row, i) => <div key={i}>{itemContent(i, row)}</div>)}</div>
  ),
}));

const now = new Date().toISOString();
const rootOf = (name: string): ProjectRootInfo => ({ Name: name, ProfileName: 'Default', Actions: [] });
const session = (name: string, parent?: string, progress?: WorkProgress): ProjectSummary =>
  ({ ...project(`Default/fleet/${name}`, name, 'Idle', now), RootName: 'fleet', ParentId: parent ?? null, Progress: progress ?? null });

const initialState = useAppStore.getState();
let view: Rendered | undefined;
let hub: FakeHub;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  useAppStore.setState({ collapsedSessions: {} });
  localStorage.clear();
  history.replaceState(null, '', '#/');
  hub = new FakeHub([
    session('epic', undefined, { Resolved: 4, Total: 12 }),
    session('worker', 'Default/fleet/epic'),
    session('split', undefined, { Resolved: 0, Total: 3 }),
    session('plain'),
  ], [rootOf('fleet')]);
  await connectServers({ A: hub });
});

afterEach(() => { view?.unmount(); view = undefined; });

const q = (sel: string, from: ParentNode = view!.container) => [...from.querySelectorAll<HTMLElement>(sel)];
const rowOf = (name: string) => q('.project-item').find(r => r.querySelector('.project-name')?.textContent === name)!;
const markOf = (from: ParentNode) => from.querySelector<HTMLElement>('.progress-mark');

it("shows x/N at a parent's row's end, open or folded, beside the folded count", async () => {
  view = await render(<Shell />);

  const open = markOf(rowOf('epic'))!;
  expect(open.textContent).toBe('4/12');
  expect(open.getAttribute('aria-label')).toBe('4 of 12 resolved');
  expect(open.closest('.project-children-summary')).not.toBeNull();
  expect(rowOf('epic').querySelector('.project-children-count')).toBeNull();
  // The row has the count alone: the bar is the header's
  expect(open.querySelector('.progress-bar')).toBeNull();

  await click(rowOf('epic').querySelector<HTMLButtonElement>('.project-children-toggle')!);
  const summary = rowOf('epic').querySelector('.project-children-summary')!;
  expect([...summary.children].map(el => el.className.split(' ')[0])).toEqual(['progress-mark', 'project-children-count']);
  expect(summary.textContent).toBe('4/121');
});

it('shows it on a session with nothing under it, and nothing on one with no progress', async () => {
  view = await render(<Shell />);

  expect(markOf(rowOf('split'))?.textContent).toBe('0/3');
  expect(markOf(rowOf('plain'))).toBeNull();
  expect(rowOf('plain').querySelector('.project-children-summary')).toBeNull();
  expect(markOf(rowOf('worker'))).toBeNull();
});

it('moves with a status push, and goes when the script reports none', async () => {
  view = await render(<Shell />);

  await act(async () => hub.callbacks.onStatusChanged?.('Default/fleet/epic',
    { ...status('Default/fleet/epic', 'Idle'), Name: 'epic', RootName: 'fleet', Progress: { Resolved: 5, Total: 12 } }));
  expect(markOf(rowOf('epic'))?.textContent).toBe('5/12');

  await act(async () => hub.callbacks.onStatusChanged?.('Default/fleet/epic',
    { ...status('Default/fleet/epic', 'Idle'), Name: 'epic', RootName: 'fleet', Progress: null }));
  expect(markOf(rowOf('epic'))).toBeNull();
});

it("shows x/N with a bar in the project view's header, and nothing there without a progress", async () => {
  useAppStore.getState().selectProject('A', 'Default/fleet/epic');
  view = await render(<ProjectView serverId="A" projectId="Default/fleet/epic" />);

  const mark = markOf(view.container.querySelector('.project-header-info')!)!;
  expect(mark.querySelector('.progress-count')?.textContent).toBe('4/12');
  expect(mark.getAttribute('aria-label')).toBe('4 of 12 resolved');
  expect(mark.querySelector<HTMLElement>('.progress-bar-fill')!.style.width).toBe(`${100 * 4 / 12}%`);
  view.unmount();

  useAppStore.getState().selectProject('A', 'Default/fleet/plain');
  view = await render(<ProjectView serverId="A" projectId="Default/fleet/plain" />);
  expect(view.container.querySelector('.project-header')).not.toBeNull();
  expect(markOf(view.container)).toBeNull();
});
