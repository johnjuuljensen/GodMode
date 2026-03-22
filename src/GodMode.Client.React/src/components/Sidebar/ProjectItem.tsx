import type { ProjectSummary } from '../../signalr/types';

interface Props {
  project: ProjectSummary;
  isSelected: boolean;
  onSelect: () => void;
}

export function ProjectItem({ project, isSelected, onSelect }: Props) {
  const timeAgo = formatRelativeTime(project.UpdatedAt);
  const stateLabel = project.State === 'WaitingInput' ? 'WAIT' : project.State.slice(0, 4).toUpperCase();

  return (
    <div
      className={`project-item ${isSelected ? 'selected' : ''}`}
      onClick={onSelect}
    >
      <span className={`project-state-badge ${project.State}`}>
        {stateLabel}
      </span>
      <div className="project-info">
        <div className="project-name">{project.Name}</div>
        <div className="project-meta">
          {project.RootName && `${project.RootName} · `}{timeAgo}
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
