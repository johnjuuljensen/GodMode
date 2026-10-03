import { useEffect, useState } from 'react';
import { useAppStore } from '../store';
import { attentionSound, setAttentionSound } from '../services/hostApi';
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
  const sound = useDeviceSound();

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

/** This device's sound switch (#438), the app's to keep: null until the app has said, and while it cannot. */
function useDeviceSound() {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  useEffect(() => {
    let live = true;
    attentionSound().then(on => { if (live) setEnabled(on); })
      .catch(err => console.warn('[settings] The app did not say whether this device makes a sound:', err));
    return () => { live = false; };
  }, []);
  const set = (on: boolean) => {
    setEnabled(on);
    setAttentionSound(on).then(setEnabled).catch(err => {
      console.error('Failed to set the sound:', err);
      setEnabled(!on);
    });
  };
  return { enabled, set };
}
