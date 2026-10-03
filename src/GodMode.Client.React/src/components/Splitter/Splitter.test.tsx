// @vitest-environment jsdom
/**
 * The left panel's splitter (#436): drag, the arrow keys, Home and End resize the panel, and the inbox in it, between
 * 220 px and 60% of the window; a double-click puts it back to 320 px. The width is this device's, in localStorage,
 * and a profile window keeps its own. Tile mode and the phone have none. Renders the Shell on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, keyDown, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { Shell } from '../Shell';

const host = vi.hoisted(() => ({ window: { Profile: null as string | null, CanOpenWindows: true } }));

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  windowInfo: async () => host.window,
  openProfileWindow: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  subscribeAttentionLinks: () => () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

// The window's (max-width: 768px): a phone when set
let phone = false;
const otherMedia = window.matchMedia;
window.matchMedia = (query: string) => query !== '(max-width: 768px)' ? otherMedia(query) : ({
  matches: phone, media: query, onchange: null,
  addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {},
  dispatchEvent: () => false,
}) as unknown as MediaQueryList;

const MAIN_KEY = 'godmode-sidebar-width';
const initialState = useAppStore.getState();
let view: Rendered | undefined;

const splitter = () => view!.container.querySelector<HTMLElement>('.splitter');
const sidebar = () => view!.container.querySelector<HTMLElement>('.shell-sidebar')!;
/** The panel's width as laid out, and as the splitter says it. */
const width = () => {
  const said = Number(splitter()!.getAttribute('aria-valuenow'));
  expect(sidebar().style.width).toBe(`${said}px`);
  return said;
};

async function open(profile: string | null = null) {
  host.window = { Profile: profile, CanOpenWindows: profile === null };
  await useAppStore.getState().loadWindow();
  view = await render(<Shell />);
}

async function setWindowWidth(px: number) {
  await act(async () => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: px });
    window.dispatchEvent(new Event('resize'));
  });
}

const pointer = (type: string, clientX: number) => new MouseEvent(type, { clientX, button: 0, bubbles: true, cancelable: true });
/** Presses the splitter at `from`, moves to `to` and lets go. */
async function drag(from: number, to: number) {
  await act(async () => { splitter()!.dispatchEvent(pointer('pointerdown', from)); });
  await act(async () => { window.dispatchEvent(pointer('pointermove', (from + to) / 2)); });
  await act(async () => { window.dispatchEvent(pointer('pointermove', to)); });
  await act(async () => { window.dispatchEvent(pointer('pointerup', to)); });
}

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  localStorage.clear();
  phone = false;
  await setWindowWidth(1000);
});

afterEach(() => { view?.unmount(); view = undefined; });

describe('the left panel', () => {
  it('is 320 px wide at first, and says its bounds: 220 px, and 60% of the window', async () => {
    await open();
    const s = splitter()!;
    expect(s.getAttribute('role')).toBe('separator');
    expect(s.getAttribute('aria-orientation')).toBe('vertical');
    expect(s.tabIndex).toBe(0);
    expect(width()).toBe(320);
    expect(s.getAttribute('aria-valuemin')).toBe('220');
    expect(s.getAttribute('aria-valuemax')).toBe('600');
  });

  it('follows a drag, and keeps the width', async () => {
    await open();
    await drag(320, 450);
    expect(width()).toBe(450);
    expect(localStorage.getItem(MAIN_KEY)).toBe('450');
    expect(document.body.classList.contains('splitter-dragging')).toBe(false);
  });

  it('stops at the minimum when dragged past it', async () => {
    await open();
    await drag(320, 10);
    expect(width()).toBe(220);
    expect(localStorage.getItem(MAIN_KEY)).toBe('220');
  });

  it('stops at 60% of the window when dragged past it', async () => {
    await open();
    await drag(320, 990);
    expect(width()).toBe(600);
    expect(localStorage.getItem(MAIN_KEY)).toBe('600');
  });

  it('a move after letting go resizes nothing', async () => {
    await open();
    await drag(320, 400);
    await act(async () => { window.dispatchEvent(pointer('pointermove', 500)); });
    expect(width()).toBe(400);
  });

  it('steps with the arrow keys on the focused splitter, further with Shift, to the bounds with Home and End', async () => {
    await open();
    const s = splitter()!;
    await act(async () => s.focus());
    expect(document.activeElement).toBe(s);
    expect((await keyDown(s, 'ArrowRight')).defaultPrevented).toBe(true);
    expect(width()).toBe(336);
    await keyDown(s, 'ArrowLeft');
    await keyDown(s, 'ArrowLeft');
    expect(width()).toBe(304);
    await keyDown(s, 'ArrowRight', { shiftKey: true });
    expect(width()).toBe(368);
    await keyDown(s, 'Home');
    expect(width()).toBe(220);
    await keyDown(s, 'ArrowLeft');
    expect(width()).toBe(220);
    await keyDown(s, 'End');
    expect(width()).toBe(600);
    await keyDown(s, 'ArrowRight');
    expect(width()).toBe(600);
    expect(localStorage.getItem(MAIN_KEY)).toBe('600');
    // Any other key is the page's
    expect((await keyDown(s, 'a')).defaultPrevented).toBe(false);
  });

  it('goes back to 320 px on a double-click, and forgets the width', async () => {
    await open();
    await drag(320, 500);
    await act(async () => { splitter()!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true })); });
    expect(width()).toBe(320);
    expect(localStorage.getItem(MAIN_KEY)).toBeNull();
  });

  it('opens at the width it was left at', async () => {
    localStorage.setItem(MAIN_KEY, '410');
    await open();
    expect(width()).toBe(410);
  });

  it.each(['wide', '', '-40', '0', 'NaN', 'Infinity', '{}'])('reads a stored %j as no width: 320 px', async stored => {
    localStorage.setItem(MAIN_KEY, stored);
    await open();
    expect(width()).toBe(320);
  });

  it('fits a stored width to the bounds, and keeps it for a wider window', async () => {
    localStorage.setItem(MAIN_KEY, '700');
    await open();
    expect(width()).toBe(600);
    await setWindowWidth(800);
    expect(width()).toBe(480);
    expect(splitter()!.getAttribute('aria-valuemax')).toBe('480');
    await setWindowWidth(1400);
    expect(width()).toBe(700);
    expect(localStorage.getItem(MAIN_KEY)).toBe('700');
  });

  it('holds the inbox, which is as wide as the panel', async () => {
    await open();
    expect(sidebar().querySelector('.inbox-pane')).not.toBeNull();
  });
});

describe("a profile window's panel", () => {
  it("keeps its own width, apart from the main window's", async () => {
    localStorage.setItem(MAIN_KEY, '410');
    await open('Work');
    expect(width()).toBe(320);
    await drag(320, 380);
    expect(width()).toBe(380);
    expect(localStorage.getItem(`${MAIN_KEY}:Work`)).toBe('380');
    expect(localStorage.getItem(MAIN_KEY)).toBe('410');
    view!.unmount();
    view = undefined;

    useAppStore.setState(initialState, true);
    await open(null);
    expect(width()).toBe(410);
  });

  it('opens at the width it was left at', async () => {
    localStorage.setItem(`${MAIN_KEY}:Work`, '450');
    await open('Work');
    expect(width()).toBe(450);
  });
});

describe('no splitter', () => {
  it('in tile mode', async () => {
    useAppStore.setState({ isTileView: true });
    await open();
    expect(splitter()).toBeNull();
  });

  it('on a phone', async () => {
    phone = true;
    await setWindowWidth(400);
    await open();
    expect(splitter()).toBeNull();
    expect(sidebar().style.width).toBe('');
  });
});
