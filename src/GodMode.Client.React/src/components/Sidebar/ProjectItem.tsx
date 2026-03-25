import type { ProjectSummary } from '../../signalr/types';
import { useAppStore } from '../../store';

interface Props {
  project: ProjectSummary;
  isSelected: boolean;
  onSelect: () => void;
}

export function ProjectItem({ project, isSelected, onSelect }: Props) {
  const timeAgo = formatRelativeTime(project.UpdatedAt);
  const clientQuestion = useAppStore(s => s.projectQuestions[project.Id]);
  const isWaiting = project.State === 'WaitingInput' || clientQuestion;
  const stateStr = String(project.State ?? 'Idle');
  const stateLabel = isWaiting ? 'WAIT' : stateStr.slice(0, 4).toUpperCase();

  return (
    <div
      className={`project-item ${isSelected ? 'selected' : ''} ${isWaiting ? 'waiting' : ''}`}
      onClick={onSelect}
    >
      <span className={`project-state-badge ${isWaiting ? 'WaitingInput' : project.State}`}>
        {stateLabel}
      </span>
      <div className="project-info">
        <div className="project-name">{project.Name}</div>
        <div className="project-meta">
          {project.RootName && `${project.RootName} · `}{timeAgo}
          {isWaiting && project.CurrentQuestion && (
            <span className="project-question-hint" title={project.CurrentQuestion}>
              {' · '}{project.CurrentQuestion.length > 30
                ? project.CurrentQuestion.slice(0, 30) + '...'
                : project.CurrentQuestion}
            </span>
          )}
        </div>
      </div>
    </div>
  );
}

function formatRelativeTime(dateStr: string): string {
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMin = Math.floor(diffMs / 60000);

  if (diffMin < 1) return 'just now';
  if (diffMin < 60) return `${diffMin}m ago`;
  const diffHr = Math.floor(diffMin / 60);
  if (diffHr < 24) return `${diffHr}h ago`;
  const diffDays = Math.floor(diffHr / 24);
  return `${diffDays}d ago`;
}
