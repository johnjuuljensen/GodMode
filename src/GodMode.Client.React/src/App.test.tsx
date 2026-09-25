// @vitest-environment jsdom
/**
 * Every GodMode server requires its API key (#232), a loopback one included: until the browser holds a key
 * the server accepts, the app shows the key page, and once it does, the shell. The server here answers
 * /servers as GodMode.Server does: 401 without the key.
 */
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act } from 'react';
import { render, typeInto, type Rendered } from './test/render';
import { clearApiKey, getApiKey } from './services/hostApi';
import App from './App';

vi.mock('./components/Shell', () => ({ Shell: () => <div className="test-shell" /> }));
vi.mock('./store', () => ({
  useAppStore: (select: (state: { loadServers: () => Promise<void> }) => unknown) => select({ loadServers: async () => {} }),
}));

const KEY = 'the-servers-key';
let view: Rendered;

const keyPage = () => view.container.querySelector('.auth-page');
const shell = () => view.container.querySelector('.test-shell');
const keyInput = () => view.container.querySelector<HTMLInputElement>('#auth-api-key')!;
const submit = () => act(async () => {
  view.container.querySelector<HTMLFormElement>('.auth-form')!.requestSubmit();
});

beforeEach(() => {
  clearApiKey();
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) =>
    new Response('[]', { status: (init?.headers as Record<string, string>)?.Authorization === `Bearer ${KEY}` ? 200 : 401 })));
});

afterEach(() => {
  view.unmount();
  vi.unstubAllGlobals();
});

it('shows the key page to a browser that holds no key, and the shell once the key is entered', async () => {
  view = await render(<App />);
  expect(keyPage()).not.toBeNull();
  expect(shell()).toBeNull();

  await typeInto(keyInput(), KEY);
  await submit();

  expect(shell()).not.toBeNull();
  expect(getApiKey()).toBe(KEY);
});

it('says so when the server refuses the key entered', async () => {
  view = await render(<App />);

  await typeInto(keyInput(), 'not-the-key');
  await submit();

  expect(keyPage()).not.toBeNull();
  expect(view.container.querySelector('.auth-error')?.textContent).toBe('The server did not accept that key.');
});
