// @vitest-environment jsdom
/**
 * Adding a server the shell refuses (#231): the add-server screen shows the shell's reason and stays open,
 * so the user sees why (secure storage would not keep the token) instead of a form that silently does nothing.
 * Every server requires its API key (#232), so the form asks for one before it adds anything.
 */
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act } from 'react';
import { render, typeInto, type Rendered } from '../../test/render';
import { answer, sent, settle } from '../../test/appShell';
import { useAppStore } from '../../store';
import { AddServer } from './AddServer';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));

const REFUSED = "The server was not added: this device's secure storage would not keep its access token (Keystore unavailable).";

const initialState = useAppStore.getState();
let view: Rendered;

const addButton = () => [...view.container.querySelectorAll('button')].find(b => b.textContent === 'Add Server')!;
const errorText = () => view.container.querySelector('.settings-error')?.textContent ?? null;

const keyInput = () => view.container.querySelector<HTMLInputElement>('input[type="password"]')!;

const adds = () => sent.filter(m => m.Type === 'servers.add');
const clickAdd = async () => {
  await act(async () => { addButton().click(); });
  await settle();
};

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  useAppStore.getState().setActivePage({ type: 'addServer' });
  answer('relay.info', { BaseUrl: 'http://127.0.0.1:49152', Secret: 'relay-secret' });
  answer('servers.list', []);
  view = await render(<AddServer />);
});

afterEach(() => view.unmount());

it('asks for the API key of a local server before it adds one', async () => {
  expect(addButton().disabled).toBe(true);
  await clickAdd();
  expect(adds()).toEqual([]);

  await typeInto(keyInput(), '   ');
  expect(addButton().disabled).toBe(true);

  await typeInto(keyInput(), 'secret-key');
  expect(addButton().disabled).toBe(false);
});

it('shows why the shell refused the server and keeps the form open', async () => {
  await typeInto(keyInput(), 'secret-key');
  answer('servers.add', new Error(REFUSED));

  await clickAdd();

  expect(errorText()).toBe(REFUSED);
  expect(useAppStore.getState().activePage).toEqual({ type: 'addServer' });
});

it('clears the reason and closes the form when a retry succeeds', async () => {
  await typeInto(keyInput(), 'secret-key');
  answer('servers.add', new Error(REFUSED));
  await clickAdd();

  answer('servers.add', { Id: 'B' });
  await clickAdd();

  expect(errorText()).toBeNull();
  expect(adds().at(-1)?.Payload).toMatchObject({ Type: 'local', Urls: ['http://localhost:31337'], AccessToken: 'secret-key' });
  expect(useAppStore.getState().activePage).toBeNull();
});
