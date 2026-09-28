/** Voice is the Windows app's: in the browser nothing reaches for the shell, and the page shows no voice. */
import { expect, it, vi } from 'vitest';
import * as bridge from './hostBridge';
import { describeVoiceError, getVoiceStatus, withLine } from './voice';

vi.mock('./hostApi', () => ({ isMaui: false }));
vi.mock('./hostBridge', () => ({ request: vi.fn(), on: vi.fn() }));

it('is not available in the browser, without asking a shell', async () => {
  expect(await getVoiceStatus()).toEqual({ Available: false, State: 'Off', Lines: [] });
  expect(bridge.request).not.toHaveBeenCalled();
});

it('says what failed and what to do', () => {
  expect(describeVoiceError({ Service: 'Model', Kind: 'Authentication', Message: '401' }))
    .toBe('Claude refused the key: check it in the voice settings.');
  expect(describeVoiceError({ Service: 'SpeechSynthesis', Kind: 'Authentication', Message: '401' }))
    .toBe('ElevenLabs refused the key: check it in the voice settings.');
  expect(describeVoiceError({ Service: 'Model', Kind: 'ConnectionLost', Message: 'x' }))
    .toBe('Claude: connection lost, retrying.');
  expect(describeVoiceError({ Service: 'SpeechSynthesis', Kind: 'ServiceError', Message: 'quota exceeded' }))
    .toBe('ElevenLabs (speech) failed: quota exceeded');
});

it('replaces the partial line of the user with what follows it', () => {
  const partial = withLine([{ Speaker: 'Bot', Text: 'Klar.' }], { Speaker: 'User', Text: 'hvad', Partial: true });
  expect(withLine(partial, { Speaker: 'User', Text: 'hvad venter' })).toEqual([
    { Speaker: 'Bot', Text: 'Klar.' }, { Speaker: 'User', Text: 'hvad venter' },
  ]);
});
