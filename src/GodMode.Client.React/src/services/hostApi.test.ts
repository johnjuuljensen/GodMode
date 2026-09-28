// @vitest-environment jsdom
/**
 * The page's host is the app (#291): servers come from its shell, and a server's hub is reached through the app's
 * relay, with the relay's secret, never the server's key. Runs the real hostApi and hostBridge against the shell.
 */
import { expect, it, vi } from 'vitest';
import { answer, emit, sent, settle } from '../test/appShell';
import type { ServerInfo } from '../signalr/types';
import * as api from './hostApi';

const RELAY = { BaseUrl: 'http://127.0.0.1:49152', Secret: 'relay-secret' };

it('reaches a server through the relay once the app has said where it is, with the relay secret', async () => {
  answer('relay.info', RELAY);

  await api.waitUntilReady();
  await api.waitUntilReady();

  expect(sent.map(m => m.Type)).toEqual(['relay.info']);
  expect(api.getHubUrl('a b')).toBe('http://127.0.0.1:49152/?serverId=a%20b');
  const options = api.getHubOptions('a b');
  expect(options).toMatchObject({ skipNegotiation: true, transport: 1 });
  expect(options.accessTokenFactory?.()).toBe('relay-secret');
});

it('lists, adds, removes, starts and stops servers through the shell', async () => {
  const server: ServerInfo = { Id: 'A', Name: 'Box', Type: 'local', State: 'Running' };
  answer('servers.list', [server]);
  answer('servers.add', { Id: 'B' });
  answer('servers.remove', true);
  answer('servers.start', true);
  answer('servers.stop', true);

  expect(await api.fetchServers()).toEqual([server]);
  await api.addServer({ Type: 'local', DisplayName: 'Box', Url: 'http://box:31337, http://100.64.0.1:31337', AccessToken: 'key' });
  await api.removeServer('A');
  await api.startServer('A');
  await api.stopServer('A');

  expect(sent.slice(1)).toEqual([
    { Type: 'servers.add', Payload: { Type: 'local', DisplayName: 'Box', Urls: ['http://box:31337', 'http://100.64.0.1:31337'], Username: null, AccessToken: 'key' } },
    { Type: 'servers.remove', Payload: { ServerId: 'A' } },
    { Type: 'servers.start', Payload: { ServerId: 'A' } },
    { Type: 'servers.stop', Payload: { ServerId: 'A' } },
  ]);
});

it("fails a request with the shell's reason", async () => {
  answer('servers.add', new Error('secure storage would not keep the token'));

  await expect(api.addServer({ Type: 'local', DisplayName: 'Box', Url: 'http://box:31337', AccessToken: 'key' }))
    .rejects.toThrow('secure storage would not keep the token');
});

it('hears when the servers change, and opens the item a notification names', async () => {
  const changed = vi.fn();
  const open = vi.fn();
  answer('attention.take', { ServerId: 'A', ProjectId: 'p1' });

  const stopEvents = api.subscribeEvents(changed);
  const stopLinks = api.subscribeAttentionLinks(open);
  await settle();
  // The tap that launched the app, before the page loaded
  expect(open).toHaveBeenCalledWith('A', 'p1');

  answer('attention.take', { ServerId: 'A', ProjectId: 'p2' });
  await emit('attention.open');
  await emit('servers.changed');
  await settle();

  expect(open).toHaveBeenLastCalledWith('A', 'p2');
  expect(changed).toHaveBeenCalledWith('serversChanged', null);

  stopEvents();
  stopLinks();
  await emit('servers.changed');
  expect(changed).toHaveBeenCalledTimes(1);
});
