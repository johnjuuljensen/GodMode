import { useEffect, useState } from 'react';
import { getVoiceSettings, setVoiceSettings, type VoiceSettingsUpdate, type VoiceSettingsView } from '../../services/voice';
import { Toggle } from '../settings-shared';
import '../settings-common.css';

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
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ error: boolean; text: string } | null>(null);

  const show = (settings: VoiceSettingsView) => {
    setView(settings);
    setVoiceId(settings.VoiceId);
    setLanguage(settings.Language);
    setEchoCancellation(settings.EchoCancellation);
  };

  useEffect(() => {
    getVoiceSettings()
      .then(show)
      .catch(err => setMessage({ error: true, text: `Could not read the voice settings: ${err instanceof Error ? err.message : err}` }));
  }, []);

  if (!view) return message && <div className="settings-error" role="alert">{message.text}</div>;

  const save = async (update: VoiceSettingsUpdate) => {
    setSaving(true);
    setMessage(null);
    try {
      show(await setVoiceSettings(update));
      setElevenLabsKey('');
      setAnthropicKey('');
      setMessage({ error: false, text: 'Saved. A running voice session uses the new settings from its next start.' });
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
      <div className="settings-item">
        <div className="settings-item-info">
          <div className="settings-item-name">Echo cancellation</div>
          <div className="settings-item-desc">For speakers instead of a headset. Off until it is measured to hold up on laptop speakers.</div>
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
