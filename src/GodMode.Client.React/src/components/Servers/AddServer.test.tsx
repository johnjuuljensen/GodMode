// @vitest-environment jsdom
/**
 * Adding a server the shell refuses (#231): the add-server screen shows the shell's reason and stays open,
 * so the user sees why (secure storage would not keep the token) instead of a form that silently does nothing.
 * Every server requires its API key (#232), so the form asks for one before it adds anything.
 */
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act } from 'react';
import * as api from '../../services/hostApi';
import { render, typeInto, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { AddServer } from './AddServer';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  isMaui: true,
  addServer: vi.fn(),
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const REFUSED = "The server was not added: this device's secure storage would not keep its access token (Keystore unavailable).";

const initialState = useAppStore.getState();
let view: Rendered;

const addButton = () => [...view.container.querySelectorAll('button')].find(b => b.textContent === 'Add Server')!;
const errorText = () => view.container.querySelector('.settings-error')?.textContent ?? null;

const keyInput = () => view.container.querySelector<HTMLInputElement>('input[type="password"]')!;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  useAppStore.getState().setActivePage({ type: 'addServer' });
  vi.mocked(api.addServer).mockReset();
  view = await render(<AddServer />);
});

afterEach(() => view.unmount());

it('asks for the API key of a local server before it adds one', async () => {
  expect(addButton().disabled).toBe(true);
  await act(async () => { addButton().click(); });
  expect(api.addServer).not.toHaveBeenCalled();

  await typeInto(keyInput(), '   ');
  expect(addButton().disabled).toBe(true);

  await typeInto(keyInput(), 'secret-key');
  expect(addButton().disabled).toBe(false);
});

it('shows why the shell refused the server and keeps the form open', async () => {
  await typeInto(keyInput(), 'secret-key');
  vi.mocked(api.addServer).mockRejectedValueOnce(new Error(REFUSED));

  await act(async () => { addButton().click(); });

  expect(errorText()).toBe(REFUSED);
  expect(useAppStore.getState().activePage).toEqual({ type: 'addServer' });
});

it('clears the reason and closes the form when a retry succeeds', async () => {
  await typeInto(keyInput(), 'secret-key');
  vi.mocked(api.addServer).mockRejectedValueOnce(new Error(REFUSED)).mockResolvedValueOnce();
  await act(async () => { addButton().click(); });

  await act(async () => { addButton().click(); });

  expect(errorText()).toBeNull();
  expect(vi.mocked(api.addServer).mock.calls.at(-1)?.[0]).toMatchObject({ Type: 'local', AccessToken: 'secret-key' });
  expect(useAppStore.getState().activePage).toBeNull();
});
