import { useEffect } from 'react';
import { useAppStore } from '../../store';

/** The page's title: "GodMode", or "GodMode — Work" in a window locked to the Work profile (#340). */
export const windowTitle = (lockedProfile: string | null) => lockedProfile === null ? 'GodMode' : `GodMode — ${lockedProfile}`;

/**
 * Puts how many projects need the user in the document title: "(3) GodMode". In a locked page they are its
 * profile's only (the store keeps no other). The app shows the title in the window's title bar.
 */
export function useAttentionTitle() {
  const count = useAppStore(s => s.attention.length);
  const lockedProfile = useAppStore(s => s.lockedProfile);
  useEffect(() => {
    const base = windowTitle(lockedProfile);
    document.title = count > 0 ? `(${count}) ${base}` : base;
  }, [count, lockedProfile]);
}
