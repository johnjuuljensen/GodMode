import { useEffect, useState } from 'react';
import {
  getVoiceDevices, getVoiceSettings, setVoiceSettings,
  type AudioDevice, type VoiceDeviceList, type VoiceSettingsUpdate, type VoiceSettingsView,
} from '../../services/voice';
import { Toggle } from '../settings-shared';
import '../settings-common.css';

const DEFAULT: AudioDevice = { Id: '', Name: '' };

/**
 * Default (named after the device it is now), each device there is, and a chosen one that is not there now: voice
 * uses the default in its place until it is back.
 */
function DevicePicker({ label, devices, defaultId, chosen, onChange, onOpen }: {
  label: string;
  devices: AudioDevice[];
  defaultId?: string | null;
  chosen: AudioDevice;
  onChange: (device: AudioDevice) => void;
  onOpen: () => void;
}) {
  const same = (a: string, b?: string | null) => a.toLowerCase() === b?.toLowerCase();
  const byDefault = devices.find(d => same(d.Id, defaultId));
  const missing = chosen.Id && !devices.some(d => same(d.Id, chosen.Id));
  return (
    <div className="form-group">
      <label>{label}</label>
      <select aria-label={label} value={chosen.Id} onFocus={onOpen}
        onChange={e => onChange(devices.find(d => d.Id === e.target.value) ?? (e.target.value === chosen.Id ? chosen : DEFAULT))}>
        <option value="">{byDefault ? `Default (${byDefault.Name})` : 'Default'}</option>
        {devices.map(d => <option key={d.Id} value={d.Id}>{d.Name}</option>)}
        {missing && <option value={chosen.Id}>{chosen.Name} (not connected)</option>}
      </select>
    </div>
  );
}

/**
 * The voice settings, in the Windows app. A key typed here goes to the shell's secure storage and never comes back:
 * the form only learns whether each is set.
 */
export function VoiceSettings() {
  const [view, setView] = useState<VoiceSettingsView | null>(null);
  const [elevenLabsKey, setElevenLabsKey] = useState('');
  const [anthropicKey, setAnthropicKey] = useState('');
  const [voiceId, setVoiceId] = useState('');
  const [language, setLanguage] = useState('');
  const [echoCancellation, setEchoCancellation] = useState(false);
  const [devices, setDevices] = useState<VoiceDeviceList | null>(null);
  const [microphone, setMicrophone] = useState<AudioDevice>(DEFAULT);
  const [speaker, setSpeaker] = useState<AudioDevice>(DEFAULT);
  const [micSilence, setMicSilence] = useState('');
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ error: boolean; text: string } | null>(null);

  const show = (settings: VoiceSettingsView) => {
    setView(settings);
    setVoiceId(settings.VoiceId);
    setLanguage(settings.Language);
    setEchoCancellation(settings.EchoCancellation);
    setMicrophone(settings.Microphone ?? DEFAULT);
    setSpeaker(settings.Speaker ?? DEFAULT);
    setMicSilence(String(settings.MicSilenceSeconds));
  };

  // The devices as they are now: read again when a picker opens, so a headset turned on since shows
  const listDevices = () => {
    getVoiceDevices().then(list => setDevices(list ?? null)).catch(() => setDevices(null));
  };

  useEffect(() => {
    getVoiceSettings()
      .then(show)
      .catch(err => setMessage({ error: true, text: `Could not read the voice settings: ${err instanceof Error ? err.message : err}` }));
    listDevices();
  }, []);

  if (!view) return message && <div className="settings-error" role="alert">{message.text}</div>;

  const save = async (update: VoiceSettingsUpdate) => {
    setSaving(true);
    setMessage(null);
    try {
      show(await setVoiceSettings(update));
      setElevenLabsKey('');
      setAnthropicKey('');
      setMessage({ error: false, text: 'Saved. A running voice session moves to the devices now, and uses the rest from its next start.' });
    } catch (err) {
      setMessage({ error: true, text: err instanceof Error ? err.message : String(err) });
    } finally {
      setSaving(false);
    }
  };

  const update: VoiceSettingsUpdate = {
    VoiceId: voiceId,
    Language: language,
    EchoCancellation: echoCancellation,
    ...(devices?.Supported && { Microphone: microphone, Speaker: speaker }),
    ...(devices?.Supported && Number(micSilence) > 0 && { MicSilenceSeconds: Math.round(Number(micSilence)) }),
    ...(elevenLabsKey.trim() && { ElevenLabsKey: elevenLabsKey }),
    ...(anthropicKey.trim() && { AnthropicKey: anthropicKey }),
  };

  return (
    <>
      <div className="settings-header">
        <h2>Voice</h2>
      </div>

      <div className="form-group">
        <label>ElevenLabs key</label>
        <input type="password" value={elevenLabsKey} onChange={e => setElevenLabsKey(e.target.value)}
          placeholder={view.ElevenLabsKeySet ? 'Set: type a new one to replace it' : 'Not set'} autoComplete="off" />
      </div>
      <div className="form-group">
        <label>Claude key</label>
        <input type="password" value={anthropicKey} onChange={e => setAnthropicKey(e.target.value)}
          placeholder={view.AnthropicKeySet ? 'Set: type a new one to replace it' : 'Not set (an Anthropic API key)'} autoComplete="off" />
      </div>
      <div className="form-group">
        <label>ElevenLabs voice ID</label>
        <input type="text" value={voiceId} onChange={e => setVoiceId(e.target.value)} />
      </div>
      <div className="form-group">
        <label>Language</label>
        <input type="text" value={language} onChange={e => setLanguage(e.target.value)} placeholder="da-DK+en" />
        <div className="form-description">The reply language, then each language mixed in after a +: da-DK+en is Danish with English.</div>
      </div>
      {devices?.Supported && (
        <>
          <DevicePicker label="Microphone" devices={devices.Microphones} defaultId={devices.DefaultMicrophoneId}
            chosen={microphone} onChange={setMicrophone} onOpen={listDevices} />
          <DevicePicker label="Speaker" devices={devices.Speakers} defaultId={devices.DefaultSpeakerId}
            chosen={speaker} onChange={setSpeaker} onOpen={listDevices} />
          <div className="form-description">
            Default follows Windows' default device while the mic is closed, so a headset plays music at full quality, and its
            default communications device while the mic is open; a headset turned on included.
          </div>
          <div className="form-group">
            <label htmlFor="voice-mic-silence">Close the mic after (seconds of silence)</label>
            <input id="voice-mic-silence" type="number" min={1} value={micSilence} onChange={e => setMicSilence(e.target.value)} />
            <div className="form-description">The mic also closes on the Mic button, or when you say "færdig" or "done".</div>
          </div>
        </>
      )}
      <div className="settings-item">
        <div className="settings-item-info">
          <div className="settings-item-name">Echo cancellation</div>
          <div className="settings-item-desc">For speakers instead of a headset, with Default for both devices. Off until it is measured to hold up on laptop speakers.</div>
        </div>
        <Toggle checked={echoCancellation} onChange={setEchoCancellation} />
      </div>

      {message && <div className={message.error ? 'settings-error' : 'form-description'} role={message.error ? 'alert' : 'status'}>{message.text}</div>}

      <div className="settings-form-actions">
        {(view.ElevenLabsKeySet || view.AnthropicKeySet) && (
          <button className="btn btn-secondary" disabled={saving}
            onClick={() => save({ ElevenLabsKey: '', AnthropicKey: '' })}>Remove keys</button>
        )}
        <button className="btn btn-primary" onClick={() => save(update)} disabled={saving}>
          {saving ? 'Saving...' : 'Save'}
        </button>
      </div>
    </>
  );
}
