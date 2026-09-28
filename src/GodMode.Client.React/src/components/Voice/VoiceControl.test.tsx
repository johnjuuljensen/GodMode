// @vitest-environment jsdom
/**
 * Voice in the Windows app (#285): the button shows the session's state, the transcript follows it, and an error says
 * what failed and what to do, until the service recovers. The session is the shell's: the page only shows it.
 */
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act } from 'react';
import * as bridge from '../../services/hostBridge';
import type { VoiceLine, VoiceStatus } from '../../services/hostBridge';
import { click, render, typeInto, type Rendered } from '../../test/render';
import { VoiceControl } from './VoiceControl';
import { VoiceSettings } from './VoiceSettings';

vi.mock('../../services/hostApi', () => ({ isMaui: true }));
vi.mock('../../services/hostBridge', () => ({ request: vi.fn(), on: vi.fn() }));

const handlers = new Map<string, (payload: unknown) => void>();
const emit = (type: string, payload: unknown) => act(async () => handlers.get(type)!(payload));

const status = (over: Partial<VoiceStatus> = {}): VoiceStatus => ({ Available: true, State: 'Off', Lines: [], ...over });

let view: Rendered | undefined;
const container = () => view!.container;
const button = () => container().querySelector<HTMLButtonElement>('.voice-button');
const label = () => container().querySelector('.voice-label')?.textContent;
const alertText = () => container().querySelector('[role="alert"]')?.textContent ?? null;
const lines = () => [...container().querySelectorAll('.voice-line')].map(l => l.textContent);

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

it('shows the conversation so far to a page loaded again', async () => {
  answer({ 'voice.state': status({ State: 'Listening', Lines: [{ Speaker: 'Bot', Text: 'Klar.' }, { Speaker: 'User', Text: 'Hvad venter?' }] }) });
  view = await render(<VoiceControl />);

  expect(lines()).toEqual(['Klar.', 'Hvad venter?']);
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
    Models: { Light: 'l', Medium: 'm', Heavy: 'h' }, ElevenLabsKeySet: false, AnthropicKeySet: true,
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
    { VoiceId: 'v1', Language: 'da-DK+en', EchoCancellation: false, ElevenLabsKey: 'sk_eleven' });
  expect(elevenLabs.value).toBe('');
  expect(elevenLabs.placeholder).toMatch(/^Set/);
});
