// @vitest-environment jsdom
/**
 * An Error project is answered where the inbox's "open the project" lands (#240): its input takes a
 * reply, through ReplyAndResume, as the inbox's Error item does. Renders ProjectView on the real store.
 */
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { FakeHub, project, root, connectServers } from '../../test/fakeHub';
import { render, typeInto, keyDown, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { ProjectView } from './ProjectView';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hub = new FakeHub([project('p1', 'broken', 'Error', '2026-09-24T12:00:00Z')], [root]);
  await connectServers({ A: hub });
  useAppStore.getState().selectProject('A', 'p1');
  view = await render(<ProjectView serverId="A" projectId="p1" />);
});

afterEach(() => view.unmount());

it("an Error project's input is enabled and sends through ReplyAndResume", async () => {
  const input = view.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
  expect(input.disabled).toBe(false);

  await typeInto(input, 'The migration failed; retry it');
  await keyDown(input, 'Enter');

  expect(hub.replies).toEqual([{ projectId: 'p1', text: 'The migration failed; retry it' }]);
  expect(input.value).toBe('');
});
