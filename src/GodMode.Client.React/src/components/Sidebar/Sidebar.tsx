import { useState } from 'react';
import { useAppStore, type ProfileGroup, type RootGroup, type ServerConnection, type SidebarGroupBy } from '../../store';
import { ProjectItem } from './ProjectItem';
import { isMaui } from '../../services/hostApi';
import './Sidebar.css';

const GROUP_LABELS: Record<SidebarGroupBy, string> = {
  profile: 'Profile',
  root: 'Root',
  recent: 'Recent',
  status: 'Status',
};

export function Sidebar() {
  const profileGroups = useAppStore(s => s.profileGroups);
  const inactiveServers = useAppStore(s => s.inactiveServers);
  const setShowAddServer = useAppStore(s => s.setShowAddServer);
  const setShowCreateProject = useAppStore(s => s.setShowCreateProject);
  const serverConnections = useAppStore(s => s.serverConnections);
  const sidebarGroupBy = useAppStore(s => s.sidebarGroupBy);
  const cycleSidebarGroupBy = useAppStore(s => s.cycleSidebarGroupBy);

  const hasRoots = serverConnections.some(c => c.roots.length > 0);
  const hasAnything = profileGroups.length > 0 || inactiveServers.length > 0;

  return (
    <div className="sidebar">
      <div className="sidebar-header">
        <span className="sidebar-title">GodMode</span>
        <div className="sidebar-header-actions">
          <button
            className="sidebar-group-btn"
            onClick={cycleSidebarGroupBy}
            title={`Group by: ${GROUP_LABELS[sidebarGroupBy]} (click to cycle)`}
          >
            {GROUP_LABELS[sidebarGroupBy]}
          </button>
          {hasRoots && (
            <button className="sidebar-add-btn" onClick={() => setShowCreateProject(true)} title="Create project">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
                <line x1="12" y1="8" x2="12" y2="14" /><line x1="9" y1="11" x2="15" y2="11" />
              </svg>
            </button>
          )}
          {isMaui && (
            <button className="sidebar-add-btn" onClick={() => setShowAddServer(true)} title="Add server">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <rect x="2" y="2" width="20" height="8" rx="2" ry="2" />
                <rect x="2" y="14" width="20" height="8" rx="2" ry="2" />
                <line x1="6" y1="6" x2="6.01" y2="6" /><line x1="6" y1="18" x2="6.01" y2="18" />
              </svg>
            </button>
          )}
        </div>
      </div>

      <div className="sidebar-content">
        {!hasAnything ? (
          <div className="sidebar-empty">
            <p>No servers configured</p>
            {isMaui && <button className="btn btn-primary" onClick={() => setShowAddServer(true)}>Add Server</button>}
          </div>
        ) : (
          <>
            {profileGroups.map(group => (
              <ProfileSection key={group.name} group={group} />
            ))}
            {inactiveServers.length > 0 && (
              <InactiveSection servers={inactiveServers} />
            )}
          </>
        )}
      </div>
    </div>
  );
}

function ProfileSection({ group }: { group: ProfileGroup }) {
  return (
    <div className="profile-group">
      <div className="profile-group-header">
        <span className="profile-group-name">{group.name}</span>
        <span className="profile-group-count">{group.projectCount}</span>
      </div>
      {group.rootGroups.map(rg => (
        <RootSection key={`${rg.serverId}:${rg.rootName}`} rootGroup={rg} />
      ))}
    </div>
  );
}

function RootSection({ rootGroup }: { rootGroup: RootGroup }) {
  const selectedProject = useAppStore(s => s.selectedProject);
  const selectProject = useAppStore(s => s.selectProject);
  const setShowCreateProject = useAppStore(s => s.setShowCreateProject);

  return (
    <div className="root-group">
      {!rootGroup.flat && (
        <div className="root-group-header">
          <span className="root-group-name">{rootGroup.name}</span>
          {rootGroup.actions.length > 0 && (
            <button
              className="root-action-btn"
              onClick={() => setShowCreateProject(true, { serverId: rootGroup.serverId, rootName: rootGroup.rootName })}
              title="New project"
            >+</button>
          )}
        </div>
      )}
      <div className={rootGroup.flat ? 'project-list project-list-flat' : 'project-list'}>
        {rootGroup.projects.length === 0 ? (
          !rootGroup.flat && <div className="project-list-empty">No projects</div>
        ) : (
          rootGroup.projects.map(project => (
            <ProjectItem
              key={project.Id}
              project={project}
              serverId={rootGroup.serverId}
              isSelected={
                selectedProject?.serverId === rootGroup.serverId &&
                selectedProject?.projectId === project.Id
              }
              onSelect={() => selectProject(rootGroup.serverId, project.Id)}
            />
          ))
        )}
      </div>
    </div>
  );
}

function InactiveSection({ servers }: { servers: ServerConnection[] }) {
  const connectServer = useAppStore(s => s.connectServer);
  const startServer = useAppStore(s => s.startServer);
  const setEditServerId = useAppStore(s => s.setEditServerId);
  const [pendingStarts, setPendingStarts] = useState<Set<string>>(new Set());

  const handleStart = (serverId: string) => {
    setPendingStarts(prev => new Set(prev).add(serverId));
    startServer(serverId);
  };

  return (
    <div className="inactive-section">
      <div className="profile-group-header">
        <span className="profile-group-name">Inactive</span>
      </div>
      {servers.map(conn => {
        const info = conn.serverInfo;
        const isConnecting = conn.connectionState === 'connecting' || conn.connectionState === 'reconnecting';
        const isStarting = info.State === 'Starting' || pendingStarts.has(info.Id);
        const isStopped = info.State === 'Stopped' || info.State === 'Unknown';
        const canStart = isStopped && info.Type === 'github' && !isStarting;
        const canConnect = !isConnecting && !isStarting
          && !(info.Type === 'github' && isStopped);

        // Clear pending once the server state catches up
        if (info.State !== 'Stopped' && info.State !== 'Unknown' && pendingStarts.has(info.Id)) {
          setPendingStarts(prev => { const next = new Set(prev); next.delete(info.Id); return next; });
        }

        return (
          <div key={info.Id} className="server-item">
            <div className={`server-dot ${isConnecting ? 'connecting' : info.State === 'Running' ? 'running' : isStarting ? 'connecting' : conn.connectionState}`} />
            <div className="server-info">
              <div className="server-name">{info.Name}</div>
              <div className="server-url">{info.Description ?? info.Url ?? info.Type}</div>
            </div>
            <div className="server-actions" style={{ opacity: 1 }}>
              {isStarting && <span className="server-status-text">Starting...</span>}
              {isConnecting && <span className="server-status-text">Connecting...</span>}
              {canStart && (
                <button className="server-action-btn" onClick={() => handleStart(info.Id)} title="Start">▶</button>
              )}
              {canConnect && (
                <button className="server-action-btn" onClick={() => connectServer(info.Id)} title="Connect">⚡</button>
              )}
              <button
                className="server-action-btn"
                onClick={() => setEditServerId(info.Id)}
                title="Settings"
              >⚙</button>
            </div>
          </div>
        );
      })}
    </div>
  );
}
