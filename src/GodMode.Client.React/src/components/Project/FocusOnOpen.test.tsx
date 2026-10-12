// @vitest-environment jsdom
/**
 * A project opened puts the keys in its composer (#559), so the user types at once: on a mouse-and-keyboard layout,
 * not on a touch keyboard (it would raise the keyboard over the transcript), and never from a field the user is typing
 * in elsewhere (the inbox's reply box, the create form), from a dialog, or from a question's options that take the
 * keys. The app hears each selection, and none once it is closed (session.open). Renders the Shell on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { FakeHub, project, root, connectServers, flush } from '../../test/fakeHub';
import { render, type Rendered } from '../../test/render';
import { answer, sent } from '../../test/appShell';
import type { PendingQuestion } from '../../signalr/types';
import { useAppStore } from '../../store';
import { Shell } from '../Shell';

// The touch keyboard is read once, as its module loads: a getter, so each test says which keyboard it has
const keyboard = vi.hoisted(() => {
  const state = { touch: false };
  const before = window.matchMedia;
  window.matchMedia = (query: string) => {
    const media = before(query);
    return query === '(hover: none) and (pointer: coarse)'
      ? { ...media, get matches() { return state.touch; } }
      : media;
  };
  return state;
});

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  subscribeAttentionLinks: () => () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const question: PendingQuestion = {
  RequestId: 'r1',
  RequestedAt: '2026-10-03T12:00:00Z',
  Questions: [{ Question: 'Which way?', Options: [{ Label: 'Left' }, { Label: 'Right' }], MultiSelect: false }],
};

const initialState = useAppStore.getState();
let view: Rendered;

async function start() {
  const hub = new FakeHub([
    project('x', 'x', 'Idle', '2026-10-03T09:00:00Z'),
    project('y', 'y', 'Idle', '2026-10-03T09:00:00Z'),
    { ...project('q', 'q', 'WaitingInput', '2026-10-03T09:00:00Z'), PendingQuestion: question },
  ], [root]);
  await connectServers({ A: hub });
  view = await render(<Shell />);
  await flush();
}

const open = async (projectId: string) => {
  await act(async () => useAppStore.getState().selectProject('A', projectId));
  await flush();
};
const composer = () => view.container.querySelector<HTMLTextAreaElement>('.project-view textarea.project-input');
const composerFocused = () => !!composer() && document.activeElement === composer();

/** A field outside the project view, focused as the user types in it: the inbox's reply box, the create form's. */
function typingElsewhere(): HTMLTextAreaElement {
  const field = document.createElement('textarea');
  document.body.appendChild(field);
  field.focus();
  return field;
}

beforeEach(() => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  answer('session.open', true);
  keyboard.touch = false;
});

afterEach(() => {
  view.unmount();
  document.body.replaceChildren();
});

it('opening a project focuses its composer, and each project opened after it', async () => {
  await start();
  await open('x');
  expect(composerFocused()).toBe(true);

  composer()!.blur();
  await open('y');
  expect(composerFocused()).toBe(true);
});

it('on a touch keyboard, opening a project leaves the keyboard down', async () => {
  keyboard.touch = true;
  await start();
  await open('x');
  expect(composer()).not.toBeNull();
  expect(composerFocused()).toBe(false);
});

it('a project opened while the user types in another field leaves the keys there', async () => {
  await start();
  const field = typingElsewhere();
  await open('x');
  expect(document.activeElement).toBe(field);
});

it('a project opened under a dialog leaves the keys to the dialog', async () => {
  await start();
  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay';
  const button = document.createElement('button');
  overlay.appendChild(button);
  document.body.appendChild(overlay);
  button.focus();
  await open('x');
  expect(document.activeElement).toBe(button);
});

it("a project whose question's options take the keys leaves them to the options", async () => {
  await start();
  await open('q');
  expect(view.container.querySelector('.question-option-active')?.textContent).toContain('Left');
  expect(composerFocused()).toBe(false);
});

it('tells the app each project selected, and none once it is closed', async () => {
  await start();
  await open('x');
  await open('y');
  await act(async () => useAppStore.getState().clearSelection());
  await flush();
  expect(sent.filter(m => m.Type === 'session.open').map(m => m.Payload)).toEqual([
    { Session: null },
    { Session: { ServerId: 'A', ProjectId: 'x' } },
    { Session: { ServerId: 'A', ProjectId: 'y' } },
    { Session: null },
  ]);
});
