import type { ProjectSummary, ClaudeMessage } from '../../signalr/types';
import { useAppStore } from '../../store';

interface Props {
  project: ProjectSummary;
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

export function ProjectTile({ project, messages, isLoading, isSelected, onSelect }: Props) {
  const state = project.State;
  const clientQuestion = useAppStore(s => s.projectQuestions[project.Id]);
  const isWaiting = state === 'WaitingInput' || clientQuestion;
  const tileState = isWaiting ? 'WaitingInput' : state;

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
          {isWaiting && <span className="tile-waiting-badge">WAITING</span>}
        </div>
        <span className="tile-time">{relativeTime(project.UpdatedAt)}</span>
      </div>
      {project.ProfileName && project.ProfileName !== 'Default' && (
        <div className="tile-profile">{project.ProfileName}{project.RootName ? ` / ${project.RootName}` : ''}</div>
      )}

      {/* Message preview area */}
      <div className="tile-messages">
        {isLoading && <div className="tile-loading" />}
        {messages.map((msg, i) => (
          <TileMessage key={i} message={msg} />
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

function TileMessage({ message }: { message: ClaudeMessage }) {
  if (message.isUserMessage) {
    return (
      <div className="tile-msg tile-msg-user">
        <span>{message.contentSummary || message.summary}</span>
      </div>
    );
  }

  return (
    <div className="tile-msg tile-msg-other">
      <span className="tile-msg-type">{message.typeDisplay}</span>
      <span>{message.contentSummary || message.summary}</span>
    </div>
  );
}
