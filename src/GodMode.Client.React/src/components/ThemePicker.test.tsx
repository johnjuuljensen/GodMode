// @vitest-environment jsdom
/**
 * The theme picker in settings (#296): a pick restyles the page at once, with no reload, and is kept for this
 * device under `godmode-theme`, where the old dark/light toggle's `dark` and `light` still work.
 */
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { click, render, type Rendered } from '../test/render';

let view: Rendered | undefined;
const options = () => [...view!.container.querySelectorAll<HTMLButtonElement>('[role="radio"]')];
const option = (name: string) => options().find(o => o.textContent === name)!;
const checked = () => options().filter(o => o.getAttribute('aria-checked') === 'true').map(o => o.textContent);
const shown = () => document.documentElement.getAttribute('data-theme');

/** The app starting on this device: a fresh page reads what is stored, then settings shows the picker. */
async function start(stored?: string) {
  if (stored !== undefined) localStorage.setItem('godmode-theme', stored);
  document.documentElement.removeAttribute('data-theme');
  vi.resetModules();
  const { applyStoredTheme } = await import('../themes');
  const { ThemePicker } = await import('./ThemePicker');
  applyStoredTheme();
  view = await render(<ThemePicker />);
}

beforeEach(() => localStorage.clear());
afterEach(() => { view?.unmount(); view = undefined; });

it('lists every theme, and a pick applies at once, without a reload', async () => {
  await start();
  expect(options().map(o => o.textContent)).toEqual(['Glass dark', 'Glass light', 'Phosphor']);
  expect(shown()).toBe('glass-dark');
  const page = view!.container;

  await click(option('Phosphor'));
  expect(shown()).toBe('phosphor');
  expect(checked()).toEqual(['Phosphor']);
  // The same page: it was not reloaded
  expect(page.isConnected).toBe(true);

  await click(option('Glass light'));
  expect(shown()).toBe('glass-light');
  expect(checked()).toEqual(['Glass light']);
});

it('keeps the pick for the next start', async () => {
  await start();
  await click(option('Phosphor'));
  expect(localStorage.getItem('godmode-theme')).toBe('phosphor');
  view!.unmount();

  await start();
  expect(shown()).toBe('phosphor');
  expect(checked()).toEqual(['Phosphor']);
});

it.each([
  ['dark', 'glass-dark', 'Glass dark'],
  ['light', 'glass-light', 'Glass light'],
  ['glass-light', 'glass-light', 'Glass light'],
  ['no-such-theme', 'glass-dark', 'Glass dark'],
  ['', 'glass-dark', 'Glass dark'],
])('a stored %j starts in %s', async (stored, id, name) => {
  await start(stored);
  expect(shown()).toBe(id);
  expect(checked()).toEqual([name]);
});
