import { memo, useMemo } from 'react';
import type { TranscriptItem, ToolCallItem } from '../../signalr/parseMessage';
import { Markdown } from './Markdown';
import { lineDiff, type DiffLine } from './lineDiff';
import { callStatus, callStatusTitle, resultLine } from './transcriptRow';

// Tool output, or a diff, past this many characters is cut, so one huge result or file cannot stall a row
const MAX_OUTPUT = 20_000;

interface Props {
  item: TranscriptItem;
  /** Whether a folded row (a tool call, thinking) is open */
  expanded: boolean;
  onToggle: (key: string) => void;
  /** The open rows among a subagent's items; passed only to a call that has them, so no other row re-renders */
  expandedKeys?: ReadonlySet<string>;
}

/** One transcript row: my message, Claude's reply, or a one-line tool call that opens to its detail. */
export const ChatMessage = memo(function ChatMessage({ item, expanded, onToggle, expandedKeys }: Props) {
  switch (item.kind) {
    case 'userText':
      return (
        <div className="ti ti-user">
          <div className="ti-user-bubble">{item.text}</div>
        </div>
      );
    case 'assistantText':
      return (
        <div className="ti ti-assistant">
          <Markdown text={item.text} />
        </div>
      );
    case 'thinking':
      return (
        <div className="ti ti-thinking">
          <button type="button" className="ti-fold" aria-expanded={expanded} onClick={() => onToggle(item.key)}>
            <Chevron open={expanded} />
            <span className="ti-fold-label">Thinking</span>
            {!expanded && <span className="ti-fold-summary">{item.text.split('\n')[0]}</span>}
          </button>
          {expanded && <div className="ti-thinking-text">{item.text}</div>}
        </div>
      );
    case 'toolCall':
      return <ToolCallRow call={item} expanded={expanded} onToggle={onToggle} expandedKeys={expandedKeys} />;
    case 'result':
      return (
        <div className={`ti ti-status ${item.isError ? 'ti-status-error' : 'ti-status-done'}`}>
          <span className="ti-badge">{item.isError ? 'ERROR' : 'DONE'}</span>
          <span className="ti-status-text">{resultLine(item.summary)}</span>
        </div>
      );
    case 'system':
      return (
        <div className={`ti ti-status ${item.isError ? 'ti-status-error' : ''}`}>
          <span className="ti-badge">{item.isError ? 'ERR' : 'SYS'}</span>
          <span className="ti-status-text">{item.summary || item.label}</span>
        </div>
      );
  }
});

function Chevron({ open }: { open: boolean }) {
  return (
    <svg className={`ti-chevron ${open ? 'open' : ''}`} width="10" height="10" viewBox="0 0 10 10" aria-hidden="true">
      <path d="M3 1.5 6.5 5 3 8.5" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

/** The diff an Edit, MultiEdit or Write makes, or null for any other call. */
function callDiff(call: ToolCallItem): DiffLine[] | null {
  const { input } = call;
  if (input.edits) return input.edits.flatMap(e => lineDiff(e.oldString, e.newString));
  if (input.oldString !== undefined || input.newString !== undefined) return lineDiff(input.oldString ?? '', input.newString ?? '');
  if (input.content !== undefined && input.filePath !== undefined) return lineDiff('', input.content);
  return null;
}

function ToolCallRow({ call, expanded, onToggle, expandedKeys }: {
  call: ToolCallItem; expanded: boolean; onToggle: (key: string) => void; expandedKeys?: ReadonlySet<string>;
}) {
  const diff = useMemo(() => callDiff(call), [call]);
  const added = diff?.filter(l => l.op === '+').length ?? 0;
  const removed = diff?.filter(l => l.op === '-').length ?? 0;
  const status = callStatus(call);

  return (
    <div className={`ti ti-tool ti-tool-${status}`}>
      <button type="button" className="ti-fold" aria-expanded={expanded} onClick={() => onToggle(call.key)}>
        <Chevron open={expanded} />
        <span className="ti-tool-name">{call.name}</span>
        <span className="ti-fold-summary">{call.summary}</span>
        {diff && (
          <span className="ti-diffstat">
            {added > 0 && <span className="ti-diffstat-add">+{added}</span>}
            {removed > 0 && <span className="ti-diffstat-del">-{removed}</span>}
          </span>
        )}
        {call.children.length > 0 && <span className="ti-tool-count">{call.children.length} steps</span>}
        <span className={`ti-tool-status ti-tool-status-${status}`} title={callStatusTitle[status]} />
      </button>
      {!expanded && call.isError && call.result && <div className="ti-tool-error-line">{call.result.summary}</div>}
      {expanded && (
        <div className="ti-tool-body">
          {call.input.description && call.input.command !== undefined && <div className="ti-tool-desc">{call.input.description}</div>}
          {call.input.command !== undefined && <pre className="ti-pre ti-command">{call.input.command}</pre>}
          {/* A subagent's first step is this prompt */}
          {call.input.prompt !== undefined && call.children.length === 0 && <div className="ti-tool-prompt">{call.input.prompt}</div>}
          {diff && <DiffView lines={diff} />}
          {call.children.length > 0 && (
            <div className="ti-children">
              {call.children.map(child => (
                <ChatMessage
                  key={child.key}
                  item={child}
                  expanded={expandedKeys?.has(child.key) ?? false}
                  onToggle={onToggle}
                  expandedKeys={child.kind === 'toolCall' && child.children.length > 0 ? expandedKeys : undefined}
                />
              ))}
            </div>
          )}
          {call.result && <ToolOutput call={call} />}
        </div>
      )}
    </div>
  );
}

function ToolOutput({ call }: { call: ToolCallItem }) {
  const text = call.result!.text;
  // A subagent's report is prose; an Edit's or Write's confirmation says nothing the diff does not
  if (call.children.length > 0 || call.input.subagentType !== undefined) return <Markdown text={text} className="ti-agent-report" />;
  if (!call.isError && (call.input.oldString !== undefined || call.input.edits || call.input.content !== undefined)) return null;
  if (text === '') return <div className="ti-tool-empty">No output</div>;
  const cut = text.length > MAX_OUTPUT;
  return (
    <pre className={`ti-pre ti-output ${call.isError ? 'ti-output-error' : ''}`}>
      {cut ? text.slice(0, MAX_OUTPUT) : text}
      {cut && `\n${moreCharacters(text.length - MAX_OUTPUT)}`}
    </pre>
  );
}

const moreCharacters = (count: number) => `… ${count} more characters`;

// The characters of a diff's text, with a newline between lines
const diffLength = (lines: DiffLine[]) => Math.max(0, lines.reduce((n, l) => n + l.text.length + 1, -1));

/** A diff's lines up to MAX_OUTPUT characters, the last cut where they run out, and how many characters are left out. */
function capDiff(lines: DiffLine[]): { shown: DiffLine[]; left: number } {
  const shown: DiffLine[] = [];
  let room = MAX_OUTPUT;
  for (const line of lines) {
    if (room <= 0) break;
    const text = line.text.slice(0, room);
    shown.push(text === line.text ? line : { ...line, text });
    room -= text.length + 1;
  }
  return { shown, left: diffLength(lines) - diffLength(shown) };
}

// A Write's diff is the whole file: it is cut as tool output is
function DiffView({ lines }: { lines: DiffLine[] }) {
  const { shown, left } = useMemo(() => capDiff(lines), [lines]);
  return (
    <pre className="ti-pre ti-diff">
      {shown.map((line, i) => (
        <div key={i} className={line.op === '+' ? 'ti-diff-add' : line.op === '-' ? 'ti-diff-del' : 'ti-diff-ctx'}>
          <span className="ti-diff-op">{line.op}</span>{line.text}
        </div>
      ))}
      {left > 0 && <div className="ti-diff-more">{moreCharacters(left)}</div>}
    </pre>
  );
}
