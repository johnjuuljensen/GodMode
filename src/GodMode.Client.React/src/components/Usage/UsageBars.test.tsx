// @vitest-environment jsdom
/**
 * Each root and profile in the left list shows its account's five-hour and weekly usage (#600), as the server reads it from
 * claude's rate_limit_events, updated as new readings are pushed; a window whose reset has passed shows as reset. Drives the
 * real store through a fake hub.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AccountUsage } from '../../signalr/types';
import { FakeHub, connectServers, project, root } from '../../test/fakeHub';
import { render, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { Sidebar } from '../Sidebar/Sidebar';

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

const hours = (n: number) => new Date(Date.now() + n * 60 * 60 * 1000).toISOString();
const reading = (fiveHour: number, fiveHourResets: string): AccountUsage => ({
  Account: 'acc1', Name: '.claude-private', ObservedAt: new Date().toISOString(),
  FiveHour: { Utilization: fiveHour, ResetsAt: fiveHourResets }, SevenDay: { Utilization: 0.21, ResetsAt: hours(72) },
});

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  localStorage.clear();
  hub = new FakeHub([project('p1', 'worker', 'Running', new Date().toISOString())],
    [{ ...root, Account: 'acc1', UsageCeiling: { FiveHour: 0.7 } }]);
  hub.profiles = [{ Name: 'Default', Account: 'acc1' }];
  hub.usage = [reading(0.27, hours(2))];
  await connectServers({ A: hub });
});

afterEach(() => { view?.unmount(); view = undefined; });

const meters = (where: string) =>
  [...view!.container.querySelectorAll(`${where} [role="meter"]`)].map(m => `${m.getAttribute('aria-label')} ${m.getAttribute('aria-valuenow')}`);

describe('the accounts usage bars', () => {
  it('shows the five-hour and weekly windows on the root and its profile, and follows a pushed reading over the ceiling', async () => {
    view = await render(<Sidebar />);
    expect(meters('.root-group-header')).toEqual(['five-hour usage 27', 'weekly usage 21']);
    expect(meters('.profile-group-header')).toEqual(['five-hour usage 27', 'weekly usage 21']);
    expect(view.container.querySelector('.root-group-header .usage-bar-ceiling')).not.toBeNull();

    await act(async () => hub.callbacks.onUsageChanged?.([reading(0.82, hours(2))]));
    expect(meters('.root-group-header')).toEqual(['five-hour usage 82', 'weekly usage 21']);
    const bars = view.container.querySelector('.root-group-header .usage-bars')!;
    expect(bars.querySelector('.usage-bar-over')).not.toBeNull();
    expect(bars.getAttribute('title')).toContain('Five-hour: 82%');
    expect(bars.getAttribute('title')).toContain('ceiling 70%: holds new work');
  });

  it('shows a window whose reset has passed as reset', async () => {
    await act(async () => hub.callbacks.onUsageChanged?.([reading(0.95, hours(-1))]));
    view = await render(<Sidebar />);
    expect(meters('.root-group-header')).toEqual(['five-hour usage 0', 'weekly usage 21']);
    const bars = view.container.querySelector('.root-group-header .usage-bars')!;
    expect(bars.querySelector('.usage-bar-over')).toBeNull();
    expect(bars.getAttribute('title')).toContain('Five-hour: 0%, reset');
  });
});
