// @vitest-environment jsdom
/**
 * Adding a server the shell refuses (#231): the add-server screen shows the shell's reason and stays open,
 * so the user sees why (secure storage would not keep the token) instead of a form that silently does nothing.
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

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  useAppStore.getState().setActivePage({ type: 'addServer' });
  vi.mocked(api.addServer).mockReset();
  view = await render(<AddServer />);
  await typeInto(view.container.querySelector<HTMLInputElement>('input[type="password"]')!, 'secret-key');
});

afterEach(() => view.unmount());

it('shows why the shell refused the server and keeps the form open', async () => {
  vi.mocked(api.addServer).mockRejectedValueOnce(new Error(REFUSED));

  await act(async () => { addButton().click(); });

  expect(errorText()).toBe(REFUSED);
  expect(useAppStore.getState().activePage).toEqual({ type: 'addServer' });
});

it('clears the reason and closes the form when a retry succeeds', async () => {
  vi.mocked(api.addServer).mockRejectedValueOnce(new Error(REFUSED)).mockResolvedValueOnce();
  await act(async () => { addButton().click(); });

  await act(async () => { addButton().click(); });

  expect(errorText()).toBeNull();
  expect(vi.mocked(api.addServer).mock.calls.at(-1)?.[0]).toMatchObject({ Type: 'local', AccessToken: 'secret-key' });
  expect(useAppStore.getState().activePage).toBeNull();
});
