// @vitest-environment jsdom
/**
 * Voice in the Windows app (#285): the button shows the session's state, the transcript follows it, and an error says
 * what failed and what to do, until the service recovers or it is dismissed. The session is the shell's: the page only
 * shows it. The transcript is folded away by default, and opened per device (#433).
 */
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act } from 'react';
import * as bridge from '../../services/hostBridge';
import type { VoiceLine, VoiceStatus } from '../../services/hostBridge';
import { click, render, typeInto, type Rendered } from '../../test/render';
import { TRANSCRIPT_OPEN_KEY, VoiceControl } from './VoiceControl';
import { VoiceSettings } from './VoiceSettings';

vi.mock('../../services/hostBridge', () => ({ request: vi.fn(), on: vi.fn() }));

const handlers = new Map<string, (payload: unknown) => void>();
const emit = (type: string, payload: unknown) => act(async () => handlers.get(type)!(payload));

const status = (over: Partial<VoiceStatus> = {}): VoiceStatus => ({ Available: true, State: 'Off', Lines: [], ...over });

let view: Rendered | undefined;
const container = () => view!.container;
const button = () => container().querySelector<HTMLButtonElement>('.voice-power-button');
const micButton = () => container().querySelector<HTMLButtonElement>('.voice-mic-button');
const label = () => container().querySelector('.voice-label')?.textContent;
const alertText = () => container().querySelector('[role="alert"] .voice-alert-text')?.textContent ?? null;
const lines = () => [...container().querySelectorAll('.voice-transcript .voice-line')].map(l => l.textContent);
const transcript = () => container().querySelector('.voice-transcript');
const transcriptToggle = () => container().querySelector<HTMLButtonElement>('.voice-transcript-toggle');
const latest = () => container().querySelector('.voice-latest')?.textContent ?? null;
const dismissButton = () => container().querySelector<HTMLButtonElement>('.voice-alert-dismiss');
/** This device has the transcript open, as a user who opened it before. */
const openTranscript = () => localStorage.setItem(TRANSCRIPT_OPEN_KEY, 'true');

/** The shell's answer to each request type; an Error is a request that fails with its message. */
function answer(responses: Record<string, unknown>) {
  vi.mocked(bridge.request).mockImplementation((async (type: string) => {
    const response = responses[type];
    if (response instanceof Error) throw response;
    return response;
  }) as typeof bridge.request);
}

beforeEach(() => {
  handlers.clear();
  vi.mocked(bridge.request).mockReset();
  vi.mocked(bridge.on).mockImplementation(((type: string, handler: (payload: unknown) => void) => {
    handlers.set(type, handler);
    return () => handlers.delete(type);
  }) as typeof bridge.on);
});

afterEach(() => {
  view?.unmount();
  view = undefined;
});

it('shows nothing where the shell has no voice', async () => {
  answer({ 'voice.state': status({ Available: false }) });
  view = await render(<VoiceControl />);

  expect(button()).toBeNull();
});

it('starts voice, then follows its state and the transcript, a partial replaced by what follows it', async () => {
  openTranscript();
  answer({ 'voice.state': status(), 'voice.start': status({ State: 'Starting' }) });
  view = await render(<VoiceControl />);
  expect(label()).toBe('Voice off');

  await click(button()!);
  expect(bridge.request).toHaveBeenCalledWith('voice.start');

  await emit('voice.stateChanged', status({ State: 'Listening' }));
  expect(label()).toBe('Listening');
  await emit('voice.response', { Speaker: 'Bot', Text: '283 har et spørgsmål.' });
  await emit('voice.transcript', { Speaker: 'User', Text: 'svar at den', Partial: true });
  expect(lines()).toEqual(['283 har et spørgsmål.', 'svar at den']);
  await emit('voice.transcript', { Speaker: 'User', Text: 'Svar at den skal bruge den eksisterende migration' });

  const said: VoiceLine[] = [
    { Speaker: 'Bot', Text: '283 har et spørgsmål.' },
    { Speaker: 'User', Text: 'Svar at den skal bruge den eksisterende migration' },
  ];
  expect(lines()).toEqual(said.map(l => l.Text));
  await emit('voice.stateChanged', status({ State: 'Thinking', Lines: said }));
  expect(label()).toBe('Thinking…');
  expect(lines()).toEqual(said.map(l => l.Text));
});

it('opens and closes the mic with the Mic button, and shows whether it is open', async () => {
  answer({
    'voice.state': status({ State: 'Listening', Mic: 'Closed', MicOnDemand: true }),
    'voice.mic.open': status({ State: 'Listening', Mic: 'Open', MicOnDemand: true }),
    'voice.mic.close': status({ State: 'Listening', Mic: 'Closed', MicOnDemand: true }),
  });
  view = await render(<VoiceControl />);
  expect(micButton()!.textContent).toBe('Mic');
  expect(micButton()!.getAttribute('aria-pressed')).toBe('false');

  await click(micButton()!);
  expect(bridge.request).toHaveBeenCalledWith('voice.mic.open');
  await emit('voice.stateChanged', status({ State: 'Listening', Mic: 'Open', MicOnDemand: true }));
  expect(micButton()!.getAttribute('aria-pressed')).toBe('true');
  expect(micButton()!.title).toBe('Close the mic');

  await click(micButton()!);
  expect(bridge.request).toHaveBeenCalledWith('voice.mic.close');
  // Closed by itself (silence, "færdig"): the shell says so
  await emit('voice.stateChanged', status({ State: 'Listening', Mic: 'Closed', MicOnDemand: true }));
  expect(micButton()!.getAttribute('aria-pressed')).toBe('false');
});

it('shows no Mic button while voice is off, nor where the mic is always open', async () => {
  answer({ 'voice.state': status({ Mic: 'Closed', MicOnDemand: true }) });
  view = await render(<VoiceControl />);
  expect(micButton()).toBeNull();

  await emit('voice.stateChanged', status({ State: 'Listening', Mic: 'Open', MicOnDemand: false }));
  expect(micButton()).toBeNull();
  expect(label()).toBe('Listening');
});

it('shows the conversation so far to a page loaded again', async () => {
  openTranscript();
  answer({ 'voice.state': status({ State: 'Listening', Lines: [{ Speaker: 'Bot', Text: 'Klar.' }, { Speaker: 'User', Text: 'Hvad venter?' }] }) });
  view = await render(<VoiceControl />);

  expect(lines()).toEqual(['Klar.', 'Hvad venter?']);
});

it('folds the transcript away by default, showing only the latest line, a partial included', async () => {
  answer({ 'voice.state': status({ State: 'Listening', Lines: [{ Speaker: 'Bot', Text: 'Klar.' }] }) });
  view = await render(<VoiceControl />);

  expect(transcript()).toBeNull();
  expect(transcriptToggle()!.getAttribute('aria-expanded')).toBe('false');
  expect(latest()).toBe('Klar.');
  await emit('voice.transcript', { Speaker: 'User', Text: 'hvad ven', Partial: true });
  expect(latest()).toBe('hvad ven');
  expect(container().querySelectorAll('.voice-latest')).toHaveLength(1);
});

it('opens the transcript with its toggle, and keeps it open on this device across a reload', async () => {
  const said: VoiceLine[] = [{ Speaker: 'Bot', Text: 'Klar.' }, { Speaker: 'User', Text: 'Hvad venter?' }];
  answer({ 'voice.state': status({ State: 'Listening', Lines: said }) });
  view = await render(<VoiceControl />);

  await click(transcriptToggle()!);
  expect(lines()).toEqual(said.map(l => l.Text));
  expect(latest()).toBeNull();
  expect(transcriptToggle()!.getAttribute('aria-expanded')).toBe('true');
  expect(localStorage.getItem(TRANSCRIPT_OPEN_KEY)).toBe('true');

  view.unmount();
  view = await render(<VoiceControl />);
  expect(lines()).toEqual(said.map(l => l.Text));

  await click(transcriptToggle()!);
  expect(transcript()).toBeNull();
  expect(localStorage.getItem(TRANSCRIPT_OPEN_KEY)).toBe('false');
});

it('shows an error while the transcript is folded, until it is dismissed, and a later one again', async () => {
  answer({ 'voice.state': status({ State: 'Listening' }) });
  view = await render(<VoiceControl />);
  expect(transcript()).toBeNull();

  await emit('voice.error', { Service: 'Model', Kind: 'ModelError', Message: 'overloaded' });
  expect(alertText()).toMatch(/^Claude could not answer \(overloaded\)/);
  await emit('voice.response', { Speaker: 'Bot', Text: 'Klar.' });
  expect(alertText()).not.toBeNull();

  await click(dismissButton()!);
  expect(alertText()).toBeNull();

  await emit('voice.recovered', { Service: 'Model' });
  await emit('voice.error', { Service: 'Model', Kind: 'ModelError', Message: 'overloaded' });
  expect(alertText()).not.toBeNull();
});

it('names what the × dismisses, and the transcript the toggle opens (#445)', async () => {
  answer({ 'voice.state': status({ State: 'Listening', Error: { Service: 'Model', Kind: 'ModelError', Message: 'overloaded' } }) });
  view = await render(<VoiceControl />);

  expect(dismissButton()!.getAttribute('aria-label')).toBe('Dismiss the voice error');
  await click(transcriptToggle()!);
  expect(transcriptToggle()!.getAttribute('aria-controls')).toBe(transcript()!.id);
  expect(transcript()!.id).not.toBe('');
});

it('says ElevenLabs refused the key, and clears it when that service recovers', async () => {
  answer({ 'voice.state': status({ State: 'Listening' }) });
  view = await render(<VoiceControl />);

  await emit('voice.error', { Service: 'SpeechRecognition', Kind: 'Authentication', Message: '401' });
  expect(alertText()).toBe('ElevenLabs refused the key: check it in the voice settings.');

  await emit('voice.recovered', { Service: 'Model' });
  expect(alertText()).not.toBeNull();
  await emit('voice.recovered', { Service: 'SpeechRecognition' });
  expect(alertText()).toBeNull();
});

it('says a lost connection is being retried', async () => {
  answer({ 'voice.state': status({ State: 'Listening', Error: { Service: 'SpeechRecognition', Kind: 'ConnectionLost', Message: 'socket closed' } }) });
  view = await render(<VoiceControl />);

  expect(alertText()).toBe('ElevenLabs (speech recognition): connection lost, retrying.');
});

it('shows why voice did not start', async () => {
  answer({ 'voice.state': status(), 'voice.start': new Error('Set the ElevenLabs key in the voice settings') });
  view = await render(<VoiceControl />);

  await click(button()!);

  expect(alertText()).toBe('Set the ElevenLabs key in the voice settings');
  expect(label()).toBe('Voice off');
});

it('sends a key the user typed, never shows one, and says only whether each is set', async () => {
  const settings = {
    Language: 'da-DK+en', VoiceId: 'v1', EchoCancellation: false,
    ElevenLabsKeySet: false, AnthropicKeySet: true,
  };
  answer({ 'voice.settings.get': settings, 'voice.settings.set': { ...settings, ElevenLabsKeySet: true } });
  view = await render(<VoiceSettings />);
  const [elevenLabs, anthropic] = container().querySelectorAll<HTMLInputElement>('input[type="password"]');
  expect(elevenLabs.placeholder).toBe('Not set');
  expect(anthropic.placeholder).toMatch(/^Set/);
  expect(anthropic.value).toBe('');

  await typeInto(elevenLabs, 'sk_eleven');
  await click([...container().querySelectorAll('button')].find(b => b.textContent === 'Save')!);

  expect(bridge.request).toHaveBeenCalledWith('voice.settings.set',
    {
      VoiceId: 'v1', Language: 'da-DK+en', EchoCancellation: false, Earcons: true, StaleHours: 24,
      TierModels: { Light: '', Medium: '', Heavy: '' }, ElevenLabsKey: 'sk_eleven',
    });
  expect(elevenLabs.value).toBe('');
  expect(elevenLabs.placeholder).toMatch(/^Set/);
});

it('offers Default and each device, keeps a chosen one that is not connected, and saves the choice', async () => {
  const laptop = { Id: '{0.0.1}.{laptop}', Name: 'Microphone Array (Realtek)' };
  const headset = { Id: '{0.0.1}.{headset}', Name: 'Headset (Shokz)' };
  const usb = { Id: '{0.0.0}.{usb}', Name: 'USB Speaker' };
  const speakers = { Id: '{0.0.0}.{speakers}', Name: 'Speakers (Realtek)' };
  const settings = {
    Language: 'da-DK+en', VoiceId: 'v1', EchoCancellation: false, Microphone: null, Speaker: usb,
    ElevenLabsKeySet: true, AnthropicKeySet: true,
  };
  answer({
    'voice.settings.get': settings,
    'voice.settings.set': { ...settings, Microphone: headset, Speaker: null },
    'voice.devices': {
      Supported: true, Microphones: [laptop, headset], Speakers: [speakers],
      DefaultMicrophoneId: laptop.Id.toUpperCase(), DefaultSpeakerId: speakers.Id,
    },
  });
  view = await render(<VoiceSettings />);
  const picker = (name: string) => container().querySelector<HTMLSelectElement>(`select[aria-label="${name}"]`)!;
  const options = (name: string) => [...picker(name).options].map(o => o.textContent);

  expect(options('Microphone')).toEqual(['Default (Microphone Array (Realtek))', laptop.Name, headset.Name]);
  expect(picker('Microphone').value).toBe('');
  expect(options('Speaker')).toEqual(['Default (Speakers (Realtek))', speakers.Name, 'USB Speaker (not connected)']);
  expect(picker('Speaker').value).toBe(usb.Id);

  await act(async () => {
    picker('Microphone').value = headset.Id;
    picker('Microphone').dispatchEvent(new Event('change', { bubbles: true }));
    picker('Speaker').value = '';
    picker('Speaker').dispatchEvent(new Event('change', { bubbles: true }));
  });
  await click([...container().querySelectorAll('button')].find(b => b.textContent === 'Save')!);

  expect(bridge.request).toHaveBeenCalledWith('voice.settings.set', {
    VoiceId: 'v1', Language: 'da-DK+en', EchoCancellation: false, Earcons: true, StaleHours: 24, Microphone: headset, Speaker: { Id: '', Name: '' },
    TierModels: { Light: '', Medium: '', Heavy: '' },
  });
  expect(picker('Microphone').value).toBe(headset.Id);
  expect(picker('Speaker').value).toBe('');
});

it('saves how many seconds of silence close the mic', async () => {
  const settings = {
    Language: 'da-DK+en', VoiceId: 'v1', EchoCancellation: false, Microphone: null, Speaker: null, MicSilenceSeconds: 10,
    ElevenLabsKeySet: true, AnthropicKeySet: true,
  };
  answer({
    'voice.settings.get': settings,
    'voice.settings.set': { ...settings, MicSilenceSeconds: 20 },
    'voice.devices': { Supported: true, Microphones: [], Speakers: [] },
  });
  view = await render(<VoiceSettings />);
  const silence = container().querySelector<HTMLInputElement>('#voice-mic-silence')!;
  expect(silence.value).toBe('10');

  await typeInto(silence, '20');
  await click([...container().querySelectorAll('button')].find(b => b.textContent === 'Save')!);

  expect(bridge.request).toHaveBeenCalledWith('voice.settings.set', expect.objectContaining({ MicSilenceSeconds: 20 }));
  expect(silence.value).toBe('20');
});

it('turns the sounds off', async () => {
  const settings = {
    Language: 'da-DK+en', VoiceId: 'v1', EchoCancellation: false, Earcons: true,
    ElevenLabsKeySet: true, AnthropicKeySet: true,
  };
  answer({ 'voice.settings.get': settings, 'voice.settings.set': { ...settings, Earcons: false } });
  view = await render(<VoiceSettings />);
  const items = [...container().querySelectorAll('.settings-item')];
  const sounds = items.find(i => i.querySelector('.settings-item-name')?.textContent === 'Sounds')!;
  const toggle = sounds.querySelector<HTMLButtonElement>('.settings-toggle')!;
  expect(toggle.classList.contains('on')).toBe(true);

  await click(toggle);
  await click([...container().querySelectorAll('button')].find(b => b.textContent === 'Save')!);

  expect(bridge.request).toHaveBeenCalledWith('voice.settings.set', expect.objectContaining({ Earcons: false }));
  expect(sounds.querySelector('.settings-toggle')!.classList.contains('off')).toBe(true);
});

it('saves how long voice keeps a quiet session in its lists', async () => {
  const settings = {
    Language: 'da-DK+en', VoiceId: 'v1', EchoCancellation: false, Earcons: true, StaleHours: 24,
    ElevenLabsKeySet: true, AnthropicKeySet: true,
  };
  answer({ 'voice.settings.get': settings, 'voice.settings.set': { ...settings, StaleHours: 48 } });
  view = await render(<VoiceSettings />);
  const stale = container().querySelector<HTMLInputElement>('#voice-stale-hours')!;
  expect(stale.value).toBe('24');

  await typeInto(stale, '48');
  await click([...container().querySelectorAll('button')].find(b => b.textContent === 'Save')!);

  expect(bridge.request).toHaveBeenCalledWith('voice.settings.set', expect.objectContaining({ StaleHours: 48 }));
  expect(stale.value).toBe('48');
});

it("sets a tier's model in place of VoiceBot's default, and shows the default beside it", async () => {
  const settings = {
    Language: 'da-DK+en', VoiceId: 'v1', EchoCancellation: false, Earcons: true, StaleHours: 24,
    DefaultTierModels: { Light: 'claude-haiku-5-5', Medium: 'claude-sonnet-5-5', Heavy: 'claude-opus-5-5' },
    ElevenLabsKeySet: true, AnthropicKeySet: true,
  };
  answer({ 'voice.settings.get': settings, 'voice.settings.set': { ...settings, TierModels: { Medium: 'claude-haiku-5-5' } } });
  view = await render(<VoiceSettings />);
  const medium = container().querySelector<HTMLInputElement>('#voice-model-Medium')!;
  expect(medium.value).toBe('');
  expect(medium.placeholder).toBe('Default (claude-sonnet-5-5)');
  expect(container().textContent).not.toContain('In place of the default');

  await typeInto(medium, 'claude-haiku-5-5');
  await click([...container().querySelectorAll('button')].find(b => b.textContent === 'Save')!);

  expect(bridge.request).toHaveBeenCalledWith('voice.settings.set',
    expect.objectContaining({ TierModels: { Light: '', Medium: 'claude-haiku-5-5', Heavy: '' } }));
  expect(medium.value).toBe('claude-haiku-5-5');
  expect(container().textContent).toContain('In place of the default, claude-sonnet-5-5.');
});

it('offers no devices where voice picks its own route', async () => {
  answer({
    'voice.settings.get': {
      Language: 'da-DK+en', VoiceId: 'v1', EchoCancellation: false,
      ElevenLabsKeySet: true, AnthropicKeySet: true,
    },
    'voice.devices': { Supported: false, Microphones: [], Speakers: [] },
  });
  view = await render(<VoiceSettings />);

  expect(container().querySelector('select')).toBeNull();
  expect(bridge.request).toHaveBeenCalledWith('voice.devices');
});
