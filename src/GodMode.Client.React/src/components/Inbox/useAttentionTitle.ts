import { useEffect } from 'react';
import { useAppStore } from '../../store';

/**
 * The page's title (#355): "GodMode", or "Work - GodMode" in a window locked to the Work profile (#340), the profile
 * first so a narrow taskbar button still shows it. How many projects need the user goes in front of "GodMode" in the
 * main window, "(3) GodMode", and after the profile in a locked one, "Work (3) - GodMode": the profile stays first,
 * and the count sits beside the profile it counts. AppWindows.cs gives a window the same title before its page loads.
 */
export const windowTitle = (lockedProfile: string | null, count = 0) => {
  const waiting = count > 0 ? `(${count}) ` : '';
  return lockedProfile === null ? `${waiting}GodMode` : `${lockedProfile} ${waiting}- GodMode`;
};

/**
 * Puts how many projects need the user in the document title. In a locked page they are its profile's only (the
 * store keeps no other). The app shows the title in the window's title bar.
 */
export function useAttentionTitle() {
  const count = useAppStore(s => s.attention.length);
  const lockedProfile = useAppStore(s => s.lockedProfile);
  useEffect(() => {
    document.title = windowTitle(lockedProfile, count);
  }, [count, lockedProfile]);
}
