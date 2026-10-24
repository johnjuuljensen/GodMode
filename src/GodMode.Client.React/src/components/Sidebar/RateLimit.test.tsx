// @vitest-environment jsdom
/**
 * A session its account's rate limit holds (#596) shows so in the left list, with the time the limit resets, and shows
 * nothing once the server has sent it on. Drives the real store through a fake hub.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectStatus } from '../../signalr/types';
import { FakeHub, connectServers, project, root, status } from '../../test/fakeHub';
import { render, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { Sidebar } from './Sidebar';
import { resetTime } from '../RateLimit/resetTime';

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
let view: Rendered | undefined;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  localStorage.clear();
  hub = new FakeHub([project('p1', 'worker', 'Running', new Date().toISOString())], [root]);
  await connectServers({ A: hub });
});

afterEach(() => { view?.unmount(); view = undefined; });

const push = (s: ProjectStatus) => act(async () => hub.callbacks.onStatusChanged?.('p1', s));
const mark = () => view!.container.querySelector('.project-item .rate-limit-mark');

describe('a session its account rate limit holds', () => {
  it('shows it is limited until the reset, and nothing once it is sent on', async () => {
    view = await render(<Sidebar />);
    expect(mark()).toBeNull();

    const until = new Date(Date.now() + 60 * 60 * 1000).toISOString();
    const limited = { ...status('p1', 'Idle'), Name: 'worker', UpdatedAt: new Date().toISOString() };
    await push({ ...limited, RateLimited: { Until: until, Window: 'five_hour', Since: new Date().toISOString(), Stopped: true } });
    expect(mark()?.textContent).toBe(`limited until ${resetTime(until)}`);
    expect(mark()?.getAttribute('title')).toContain('carries on at');

    await push({ ...limited, RateLimited: null });
    expect(mark()).toBeNull();
  });

  it('says the day too for a reset on another day', () => {
    const now = new Date(2026, 9, 24, 12, 0);
    expect(resetTime(new Date(2026, 9, 24, 15, 50).toISOString(), now)).toBe('15:50');
    expect(resetTime(new Date(2026, 9, 26, 15, 50).toISOString(), now)).toMatch(/^\S+ 15:50$/);
  });
});
