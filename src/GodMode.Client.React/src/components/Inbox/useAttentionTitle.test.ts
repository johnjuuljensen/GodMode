import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { windowTitle } from './useAttentionTitle';

describe('windowTitle', () => {
  it('is GodMode in the main window, the count in front', () => {
    expect(windowTitle(null)).toBe('GodMode');
    expect(windowTitle(null, 0)).toBe('GodMode');
    expect(windowTitle(null, 2)).toBe('(2) GodMode');
  });

  it('puts the profile first in a locked window, the count after it', () => {
    expect(windowTitle('Mega')).toBe('Mega - GodMode');
    expect(windowTitle('Mega', 0)).toBe('Mega - GodMode');
    expect(windowTitle('Mega', 2)).toBe('Mega (2) - GodMode');
  });

  // The app titles a window before its page loads (AppWindows.Title): read from its source, it must be the page's
  it("is the app's first title, with no count", () => {
    const source = readFileSync(join(import.meta.dirname, '..', '..', '..', '..', 'GodMode.Maui', 'AppWindows.cs'), 'utf8');
    const m = /string Title\(string\? profile\) => profile is null \? "([^"]*)" : \$"([^"]*)";/.exec(source);
    expect(m).not.toBeNull();
    const [, main, locked] = m!;
    expect(main).toBe(windowTitle(null));
    expect(locked.replace('{profile}', 'Mega')).toBe(windowTitle('Mega'));
  });
});
