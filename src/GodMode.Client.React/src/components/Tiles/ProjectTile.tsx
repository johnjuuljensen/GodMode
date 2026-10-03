import { useMemo, useState } from 'react';
import type { ProjectSummary, ClaudeMessage } from '../../signalr/types';
import { createTranscriptBuilder, type TranscriptItem } from '../../signalr/parseMessage';
import { callStatus, callStatusTitle, isConversation, resultLine } from '../Project/transcriptRow';
import { waitsOnUser } from '../../store';
import { KindLabel } from '../KindLabel/KindLabel';
import './ProjectTile.css';

// A tile shows a few lines of a row: text past this is left out of the page, not only out of sight
const MAX_PREVIEW = 400;
const preview = (text: string) => (text.length > MAX_PREVIEW ? text.slice(0, MAX_PREVIEW) : text);

interface Props {
  project: ProjectSummary;
  /** Its root as it is shown (#434): the root's title, else its name. */
  rootShown?: string;
  messages: ClaudeMessage[];
  isLoading: boolean;
  isSelected: boolean;
  onSelect: () => void;
}

function relativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return 'now';
  if (mins < 60) return `${mins}m`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h`;
  return `${Math.floor(hrs / 24)}d`;
}

export function ProjectTile({ project, rootShown, messages, isLoading, isSelected, onSelect }: Props) {
  const state = project.State;
  const isWaiting = waitsOnUser(project);
  const tileState = isWaiting ? 'WaitingInput' : state;

  // The tail read as the transcript reads it, in its simple view: a tool's result is inside its call, not a message of mine
  const [buildTranscript] = useState(createTranscriptBuilder);
  const items = useMemo(() => buildTranscript(messages).filter(isConversation), [buildTranscript, messages]);

  return (
    <button
      className={`tile tile-state-${tileState} ${isSelected ? 'tile-selected' : ''}`}
      onClick={onSelect}
    >
      {/* Header */}
      <div className="tile-header">
        <div className="tile-header-left">
          <span className={`tile-dot ${tileState}`} />
          <span className="tile-name">{project.Name}</span>
          <KindLabel kind={project.Kind} />
          {isWaiting && <span className="tile-waiting-badge">WAITING</span>}
        </div>
        <span className="tile-time">{relativeTime(project.UpdatedAt)}</span>
      </div>
      {project.ProfileName && project.ProfileName !== 'Default' && (
        <div className="tile-profile">{project.ProfileName}{project.RootName ? ` / ${rootShown ?? project.RootName}` : ''}</div>
      )}

      {/* Message preview area */}
      <div className="tile-messages">
        {isLoading && <div className="tile-loading" />}
        {items.map(item => (
          <TileItem key={item.key} item={item} />
        ))}
      </div>

      {/* Question overlay */}
      {(project.CurrentQuestion || isWaiting) && (
        <div className="tile-question">
          {project.CurrentQuestion || 'Waiting for input...'}
        </div>
      )}
    </button>
  );
}

/** A transcript row in a few lines: text clamped, a tool call on one line, as it is folded in the transcript. */
function TileItem({ item }: { item: TranscriptItem }) {
  switch (item.kind) {
    case 'userText':
      return (
        <div className="tile-msg tile-msg-user">
          <span className="tile-msg-text">{preview(item.text)}</span>
        </div>
      );
    case 'assistantText':
      return (
        <div className="tile-msg tile-msg-other">
          <span className="tile-msg-text">{preview(item.text)}</span>
        </div>
      );
    case 'thinking':
      return null;
    case 'toolCall': {
      const status = callStatus(item);
      return (
        <div className={`tile-tool tile-tool-${status}`}>
          <div className="tile-tool-line">
            <span className="tile-tool-name">{item.name}</span>
            <span className="tile-tool-summary">{item.summary}</span>
            <span className={`tile-tool-status tile-tool-status-${status}`} title={callStatusTitle[status]} />
          </div>
          {status === 'error' && <div className="tile-tool-error-line">{preview(item.result!.summary)}</div>}
        </div>
      );
    }
    case 'result':
      return (
        <div className={`tile-status ${item.isError ? 'tile-status-error' : ''}`}>
          <span className="tile-badge">{item.isError ? 'ERROR' : 'DONE'}</span>
          <span className="tile-status-text">{preview(resultLine(item.summary))}</span>
        </div>
      );
    case 'marker':
      return <div className="tile-marker" role="separator" aria-label={item.label}>{item.label}</div>;
    case 'system':
      return (
        <div className={`tile-status ${item.isError ? 'tile-status-error' : ''}`}>
          <span className="tile-badge">{item.isError ? 'ERR' : 'SYS'}</span>
          <span className="tile-status-text">{preview(item.summary || item.label)}</span>
        </div>
      );
  }
}
