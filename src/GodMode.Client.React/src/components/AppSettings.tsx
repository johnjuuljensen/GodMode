import { useEffect, useState } from 'react';
import { useAppStore } from '../store';
import { attentionSound, attentionToasts, setAttentionSound, setAttentionToasts } from '../services/hostApi';
import { Toggle } from './settings-shared';
import { VoiceSettings } from './Voice/VoiceSettings';
import { ThemePicker } from './ThemePicker';
import { useVoice } from '../services/voice';
import './settings-common.css';

const FLAGS: { key: 'featureProfiles'; label: string; desc: string }[] = [
  { key: 'featureProfiles', label: 'Profiles',      desc: 'Show profile filter' },
];

export function AppSettings() {
  const featureProfiles = useAppStore(s => s.featureProfiles);
  const setFeatureFlag = useAppStore(s => s.setFeatureFlag);

  const values: Record<string, boolean> = { featureProfiles };
  const voice = useVoice();
  const sound = useDeviceSwitch(attentionSound, setAttentionSound, 'whether this device makes a sound');
  const toasts = useDeviceSwitch(attentionToasts, setAttentionToasts, 'whether this device toasts what notifies');

  return (
    <>
      <div className="settings-header">
        <h2>Settings</h2>
      </div>
      <div className="settings-list">
        {FLAGS.map(f => (
          <div key={f.key} className="settings-item">
            <div className="settings-item-info">
              <div className="settings-item-name">{f.label}</div>
              <div className="settings-item-desc">{f.desc}</div>
            </div>
            <Toggle checked={values[f.key]} onChange={v => setFeatureFlag(f.key, v)} />
          </div>
        ))}
      </div>
      {sound.enabled !== null && (
        <div className="settings-list">
          <div className="settings-item">
            <div className="settings-item-info">
              <div className="settings-item-name">Sound for important sessions</div>
              <div className="settings-item-desc">On this device: a sound, or a heads-up on the phone, when an important session needs you</div>
            </div>
            <Toggle checked={sound.enabled} onChange={sound.set} />
          </div>
          {toasts.enabled !== null && (
            <div className="settings-item">
              <div className="settings-item-info">
                <div className="settings-item-name">Toasts for every session</div>
                <div className="settings-item-desc">On this device: what needs you from a session that is not important goes silently into Windows' notification centre. Important ones always toast</div>
              </div>
              <Toggle checked={toasts.enabled} onChange={toasts.set} />
            </div>
          )}
        </div>
      )}
      <div className="settings-header settings-section-header">
        <h2>Theme</h2>
      </div>
      <ThemePicker />
      {voice?.Available && <VoiceSettings />}
    </>
  );
}

/**
 * One of this device's switches the app keeps: its sound for what interrupts (#438), its toasts for what notifies (#491).
 * Null until the app has said, while it cannot, and where the app has no such switch.
 */
function useDeviceSwitch(get: () => Promise<boolean | null>, put: (on: boolean) => Promise<boolean | null>, what: string) {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  useEffect(() => {
    let live = true;
    get().then(on => { if (live) setEnabled(on); })
      .catch(err => console.warn(`[settings] The app did not say ${what}:`, err));
    return () => { live = false; };
  }, [get, what]);
  const set = (on: boolean) => {
    setEnabled(on);
    put(on).then(setEnabled).catch(err => {
      console.error(`Failed to set ${what}:`, err);
      setEnabled(!on);
    });
  };
  return { enabled, set };
}
