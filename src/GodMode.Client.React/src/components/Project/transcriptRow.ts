/** What a transcript row shows in one line, shared by the transcript and a tile's preview of it. */
import type { ToolCallItem, TranscriptItem } from '../../signalr/parseMessage';

export type CallStatus = 'pending' | 'error' | 'ok';

/** A call runs until its result arrives, which says whether it failed. */
export const callStatus = (call: ToolCallItem): CallStatus => (!call.result ? 'pending' : call.isError ? 'error' : 'ok');

export const callStatusTitle: Record<CallStatus, string> = { pending: 'Running', error: 'Failed', ok: 'Done' };

/** A turn's result as one line: its first line with text, without a heading's #s. */
export const resultLine = (summary: string) => (summary.split('\n').find(l => l.trim()) ?? '').replace(/^#+\s*/, '');

/** The conversation, without session bookkeeping (system lines and results); errors still show. */
export const isConversation = (item: TranscriptItem) => (item.kind !== 'system' && item.kind !== 'result') || item.isError;
