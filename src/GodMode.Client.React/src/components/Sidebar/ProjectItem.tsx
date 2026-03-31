import { useState } from 'react';
import type { ProjectSummary, McpServerConfig } from '../../signalr/types';
import { useAppStore } from '../../store';

interface Props {
  project: ProjectSummary;
  serverId: string;
  isSelected: boolean;
  onSelect: () => void;
}

export function ProjectItem({ project, serverId, isSelected, onSelect }: Props) {
  const timeAgo = formatRelativeTime(project.UpdatedAt);
  const clientQuestion = useAppStore(s => s.projectQuestions[project.Id]);
  const isWaiting = project.State === 'WaitingInput' || clientQuestion;
  const stateStr = String(project.State ?? 'Idle');
  const stateLabel = isWaiting ? 'WAIT' : stateStr.slice(0, 4).toUpperCase();

  const profileName = project.ProfileName ?? 'Default';
  const mcpServers = useAppStore(s => s.profileMcpCache[`${serverId}:${profileName}`]);
  const mcpNames = mcpServers ? Object.keys(mcpServers) : [];

  const [showDetail, setShowDetail] = useState(false);

  return (
    <div className={`project-item-wrapper ${showDetail ? 'expanded' : ''}`}>
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
          {mcpNames.length > 0 && (
            <div className="project-mcp-badges">
              {mcpNames.map(name => (
                <span key={name} className="mcp-badge" title={`MCP: ${name}`}>{name}</span>
              ))}
            </div>
          )}
        </div>
        {mcpServers && (
          <button
            className="project-detail-btn"
            onClick={e => { e.stopPropagation(); setShowDetail(!showDetail); }}
            title="Show details"
          >
            {showDetail ? '\u25B4' : '\u25BE'}
          </button>
        )}
      </div>
      {showDetail && mcpServers && (
        <ProjectDetail mcpServers={mcpServers} />
      )}
    </div>
  );
}

function ProjectDetail({ mcpServers }: { mcpServers: Record<string, McpServerConfig> }) {
  const entries = Object.entries(mcpServers);
  if (entries.length === 0) return null;

  // Collect all env vars across MCP servers
  const allEnv: [string, string, string][] = []; // [serverName, key, value]
  for (const [name, config] of entries) {
    if (config.Env) {
      for (const [k, v] of Object.entries(config.Env)) {
        allEnv.push([name, k, v]);
      }
    }
  }

  return (
    <div className="project-detail-panel">
      <div className="project-detail-section">
        <div className="project-detail-label">MCP Servers</div>
        {entries.map(([name, config]) => (
          <div key={name} className="project-detail-mcp">
            <span className="project-detail-mcp-name">{name}</span>
            <span className="project-detail-mcp-cmd">
              {config.Command}{config.Args ? ' ' + config.Args.join(' ') : ''}
            </span>
          </div>
        ))}
      </div>
      {allEnv.length > 0 && (
        <div className="project-detail-section">
          <div className="project-detail-label">Environment Variables</div>
          {allEnv.map(([server, key, value]) => (
            <div key={`${server}:${key}`} className="project-detail-env">
              <span className="project-detail-env-key">{key}</span>
              <span className="project-detail-env-value" title={value}>
                {value.length > 20 ? value.slice(0, 8) + '...' + value.slice(-4) : value}
              </span>
              <span className="project-detail-env-from">{server}</span>
            </div>
          ))}
        </div>
      )}
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
