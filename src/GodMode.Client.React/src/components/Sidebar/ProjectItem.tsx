import { useAppStore, type SidebarItem } from '../../store';

interface Props {
  item: SidebarItem;
  isSelected: boolean;
  onSelect: () => void;
}

export function ProjectItem({ item, isSelected, onSelect }: Props) {
  const { project, serverLabel } = item;
  const timeAgo = formatRelativeTime(project.UpdatedAt);
  const clientQuestion = useAppStore(s => s.projectQuestions[item.key]);
  const isWaiting = project.State === 'WaitingInput' || clientQuestion;
  const stateStr = String(project.State ?? 'Idle');
  const stateLabel = isWaiting ? 'WAIT' : stateStr.slice(0, 4).toUpperCase();

  return (
    <div className="project-item-wrapper">
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
            {serverLabel && `${serverLabel} · `}{project.RootName && `${project.RootName} · `}{timeAgo}
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
