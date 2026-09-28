import { useSyncExternalStore } from 'react';
import { getTheme, setTheme, subscribeTheme, themes } from '../themes';

/** The theme list in settings: this device's choice, applied at once. */
export function ThemePicker() {
  const current = useSyncExternalStore(subscribeTheme, getTheme);
  return (
    <div className="settings-list theme-picker" role="radiogroup" aria-label="Theme">
      {themes.map(t => (
        <button key={t.id} type="button" role="radio" aria-checked={t.id === current}
          className={`settings-item theme-option${t.id === current ? ' selected' : ''}`}
          onClick={() => setTheme(t.id)}>
          <span className="settings-item-info">
            <span className="settings-item-name">{t.name}</span>
          </span>
          <span className={`settings-dot ${t.id === current ? 'on' : 'off'}`} />
        </button>
      ))}
    </div>
  );
}
