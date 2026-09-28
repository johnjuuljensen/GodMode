// @vitest-environment jsdom
/**
 * The page is the app's (#291): the app holds every server's key, so the page opens on the shell with no key to
 * enter, gets its servers from the app, and asks no server for anything over HTTP.
 */
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { render, type Rendered } from './test/render';
import { answer, sent, settle } from './test/appShell';
import { useAppStore } from './store';
import App from './App';

vi.mock('./components/Shell', () => ({ Shell: () => <div className="test-shell" /> }));
vi.mock('./signalr/hub', () => ({ GodModeHub: class {} }));

let view: Rendered;

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('the page fetched'); }));
});

afterEach(() => {
  view.unmount();
  vi.unstubAllGlobals();
});

it('opens on the shell and lists the servers the app holds', async () => {
  answer('relay.info', { BaseUrl: 'http://127.0.0.1:49152', Secret: 'relay-secret' });
  answer('servers.list', []);

  view = await render(<App />);
  await settle();

  expect(view.container.querySelector('.test-shell')).not.toBeNull();
  expect(sent.map(m => m.Type)).toEqual(['relay.info', 'servers.list']);
  expect(useAppStore.getState().serverConnections).toEqual([]);
  expect(fetch).not.toHaveBeenCalled();
});
