import { useCallback, useEffect, useState } from 'react';

export interface WidthBounds {
  /** The width before any is chosen, and after a reset. */
  initial: number;
  min: number;
  /** The most, as a share of the window, so the content beside keeps its room. */
  maxShare: number;
}

/** The left panel's, and the inbox's in it (#436). */
export const SIDEBAR_WIDTH: WidthBounds = { initial: 320, min: 220, maxShare: 0.6 };

/** The key a window keeps its panel's width under: the main window's, or a profile window's own (#338). */
export const sidebarWidthKey = (profile: string | null) =>
  profile === null ? 'godmode-sidebar-width' : `godmode-sidebar-width:${profile}`;

/** The stored width, else none: what is not a positive, finite number of pixels is read as none. */
export function loadWidth(key: string): number | null {
  try {
    const stored = localStorage.getItem(key);
    const px = stored === null || stored.trim() === '' ? NaN : Number(stored);
    return Number.isFinite(px) && px > 0 ? px : null;
  } catch {
    return null;
  }
}

const maxFor = (bounds: WidthBounds, windowWidth: number) => Math.max(bounds.min, Math.round(windowWidth * bounds.maxShare));
const clamp = (px: number, min: number, max: number) => Math.round(Math.min(Math.max(px, min), max));

/**
 * A width this device keeps under `key`, fitted to the bounds and the window as it is now. A stored width wider than
 * the window allows is shown narrower and kept, so a wider window gets it back.
 */
export function useStoredWidth(key: string, bounds: WidthBounds) {
  const [stored, setStored] = useState(() => loadWidth(key));
  const [keySeen, setKeySeen] = useState(key);
  // Another key (a profile window learns its profile after its first render) is read afresh
  if (key !== keySeen) {
    setKeySeen(key);
    setStored(loadWidth(key));
  }

  const [windowWidth, setWindowWidth] = useState(() => window.innerWidth);
  useEffect(() => {
    const onResize = () => setWindowWidth(window.innerWidth);
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);

  const max = maxFor(bounds, windowWidth);
  const width = clamp(stored ?? bounds.initial, bounds.min, max);

  const resize = useCallback((px: number) => {
    const next = clamp(px, bounds.min, maxFor(bounds, window.innerWidth));
    setStored(next);
    try { localStorage.setItem(key, String(next)); } catch { /* kept for this page only */ }
  }, [key, bounds]);

  const reset = useCallback(() => {
    setStored(null);
    try { localStorage.removeItem(key); } catch { /* kept for this page only */ }
  }, [key]);

  return { width, min: bounds.min, max, resize, reset };
}
