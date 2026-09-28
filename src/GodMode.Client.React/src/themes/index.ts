/**
 * The themes (#296). A theme is CSS: its tokens and any decorations of its own, in a stylesheet here scoped
 * to `[data-theme="<id>"]` on <html> (Glass dark, the default, on :root). No component asks which is active.
 *
 * The choice is this device's, in localStorage under `godmode-theme`, as the dark/light toggle kept it;
 * that toggle's `dark` and `light` still name Glass dark and Glass light, and anything else is Glass dark.
 * Nothing stored (a new device) follows the system's light or dark, as the toggle did.
 */
import './glass-dark.css';
import './glass-light.css';
import './phosphor.css';

export interface Theme {
  id: string;
  name: string;
  /** The font families the theme's tokens name, bundled (no CDN). */
  fonts: readonly string[];
  /** Loads the theme's fonts, the first time it is applied. None: they load with the app (Glass's, the default's: main.tsx). */
  loadFonts?: () => Promise<unknown>;
}

export const themes: readonly Theme[] = [
  { id: 'glass-dark', name: 'Glass dark', fonts: ['DM Sans Variable', 'DM Mono'] },
  { id: 'glass-light', name: 'Glass light', fonts: ['DM Sans Variable', 'DM Mono'] },
  { id: 'phosphor', name: 'Phosphor', fonts: ['JetBrains Mono', 'VT323'], loadFonts: () => import('./phosphor.fonts') },
];

export const defaultThemeId = 'glass-dark';
export const themeStorageKey = 'godmode-theme';

/** What the dark/light toggle stored, before there were themes. */
const legacyIds: Readonly<Record<string, string>> = { dark: 'glass-dark', light: 'glass-light' };

/** The theme a stored value names: a theme's id, a legacy `dark`/`light`, else Glass dark. */
export function resolveThemeId(stored: string | null | undefined): string {
  const id = stored ? legacyIds[stored] ?? stored : defaultThemeId;
  return themes.some(t => t.id === id) ? id : defaultThemeId;
}

function readStored(): string | null {
  try {
    return localStorage.getItem(themeStorageKey)
      ?? (window.matchMedia?.('(prefers-color-scheme: light)').matches ? 'light' : null);
  } catch { return null; }
}

let current = resolveThemeId(readStored());
const listeners = new Set<() => void>();

export const getTheme = (): string => current;

export function subscribeTheme(listener: () => void): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** Puts the stored theme on the page: at start, before the first render. */
export function applyStoredTheme(): void {
  current = resolveThemeId(readStored());
  show(current);
}

/** Switches to a theme, at once and without a reload, and keeps it for this device. */
export function setTheme(id: string): void {
  current = resolveThemeId(id);
  try { localStorage.setItem(themeStorageKey, current); } catch { /* not kept: still applied */ }
  show(current);
  listeners.forEach(l => l());
}

function show(id: string): void {
  document.documentElement.setAttribute('data-theme', id);
  void themes.find(t => t.id === id)?.loadFonts?.();
}
