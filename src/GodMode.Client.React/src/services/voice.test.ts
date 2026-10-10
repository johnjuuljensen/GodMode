/** Voice is the app's: the page asks the shell for it, and says what failed in words. */
import { expect, it, vi } from 'vitest';
import * as bridge from './hostBridge';
import type { VoiceStatus } from './hostBridge';
import { describeVoiceError, getVoiceStatus, subscribeVoiceShow, withLine } from './voice';

vi.mock('./hostBridge', () => ({ request: vi.fn(), on: vi.fn() }));

it('asks the shell whether the app has voice', async () => {
  const none: VoiceStatus = { Available: false, State: 'Off', Lines: [] };
  vi.mocked(bridge.request).mockResolvedValueOnce(none);

  expect(await getVoiceStatus()).toEqual(none);
  expect(bridge.request).toHaveBeenCalledWith('voice.state');
});

it('shows each project the user switches to by voice', () => {
  const show = vi.fn();
  subscribeVoiceShow(show);

  const [type, handler] = vi.mocked(bridge.on).mock.calls.at(-1)!;
  expect(type).toBe('voice.show');
  (handler as (p: { ServerId: string; ProjectId: string }) => void)({ ServerId: 'A', ProjectId: 'p1' });
  expect(show).toHaveBeenCalledWith('A', 'p1');
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
