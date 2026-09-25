import { useState, useRef, useEffect } from 'react';
import { useShallow } from 'zustand/react/shallow';
import {
  useAppStore,
  type ActivePage, type ProfileGroup, type RootGroup, type ServerConnection, type SidebarGroupBy,
} from '../../store';
import { ProjectItem } from './ProjectItem';
import { Inbox } from '../Inbox/Inbox';
import { isMaui, clearApiKey } from '../../services/hostApi';
import './Sidebar.css';

const GROUP_LABELS: Record<SidebarGroupBy, string> = {
  profile: 'Profile',
  root: 'Root',
  recent: 'Recent',
  status: 'Status',
};

export function SidebarHeader() {
  const setActivePage = useAppStore(s => s.setActivePage);
  const serverConnections = useAppStore(s => s.serverConnections);
  const profileFilter = useAppStore(s => s.profileFilter);
  const setProfileFilter = useAppStore(s => s.setProfileFilter);
  const profileFilterOptions = useAppStore(s => s.profileFilterOptions);
  const featureProfiles = useAppStore(s => s.featureProfiles);
  const isTileView = useAppStore(s => s.isTileView);
  const setTileView = useAppStore(s => s.setTileView);

  const showProfileFilter = featureProfiles && profileFilterOptions.length > 1;
  const hasRoots = serverConnections.some(c => c.roots.length > 0);

  return (
    <div className="sidebar-header">
      <span className="sidebar-title">GodMode</span>
      <ConnectionIndicator />
      <div className="sidebar-header-actions">
        {showProfileFilter && (
          <select
            className="sidebar-profile-filter"
            value={profileFilter}
            onChange={e => setProfileFilter(e.target.value)}
          >
            {profileFilterOptions.map(name => <option key={name} value={name}>{name}</option>)}
          </select>
        )}
        <button className="sidebar-add-btn" onClick={() => setTileView(!isTileView)} title={isTileView ? 'List view' : 'Tile view'}>
          {isTileView ? (
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <line x1="3" y1="6" x2="21" y2="6" /><line x1="3" y1="12" x2="21" y2="12" /><line x1="3" y1="18" x2="21" y2="18" />
            </svg>
          ) : (
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="3" y="3" width="7" height="7" /><rect x="14" y="3" width="7" height="7" />
              <rect x="3" y="14" width="7" height="7" /><rect x="14" y="14" width="7" height="7" />
            </svg>
          )}
        </button>
        {hasRoots && (
          <button className="sidebar-add-btn" onClick={() => setActivePage({ type: 'createProject' })} title="Create project">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
              <line x1="12" y1="8" x2="12" y2="14" /><line x1="9" y1="11" x2="15" y2="11" />
            </svg>
          </button>
        )}
        {isMaui && (
          <button className="sidebar-add-btn" onClick={() => setActivePage({ type: 'addServer' })} title="Add server">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="2" y="2" width="20" height="8" rx="2" ry="2" />
              <rect x="2" y="14" width="20" height="8" rx="2" ry="2" />
              <line x1="6" y1="6" x2="6.01" y2="6" /><line x1="6" y1="18" x2="6.01" y2="18" />
            </svg>
          </button>
        )}
      </div>
    </div>
  );
}

/**
 * Each server whose connection was lost and is being retried: small, beside the title in every layout,
 * never over the page. A tap retries now.
 */
function ConnectionIndicator() {
  // Changes when a server starts or stops reconnecting, not on each status change
  const lost = useAppStore(useShallow(s => s.serverConnections
    .filter(c => c.connectionState === 'reconnecting').map(c => c.serverInfo)));
  const retryServers = useAppStore(s => s.retryServers);
  if (lost.length === 0) return null;
  return (
    <div className="connection-indicator">
      {lost.map(info => (
        <button key={info.Id} className="connection-indicator-item" onClick={retryServers}
          title={`Connection to ${info.Name} lost, retrying. Tap to retry now.`}>
          <span className="server-dot reconnecting" />
          <span className="connection-indicator-name">{info.Name}</span>
        </button>
      ))}
    </div>
  );
}

/** withInbox: the needs-you pane above the project list (wide screens; a phone has it as its home). */
export function Sidebar({ withInbox = false }: { withInbox?: boolean }) {
  const profileGroups = useAppStore(s => s.profileGroups);
  const inactiveServers = useAppStore(s => s.inactiveServers);
  const setActivePage = useAppStore(s => s.setActivePage);
  const sidebarGroupBy = useAppStore(s => s.sidebarGroupBy);
  const cycleSidebarGroupBy = useAppStore(s => s.cycleSidebarGroupBy);

  const hasAnything = profileGroups.length > 0 || inactiveServers.length > 0;

  return (
    <div className="sidebar">
      <SidebarHeader />

      <button
        className="sidebar-sort-bar"
        onClick={cycleSidebarGroupBy}
        title={`Group by: ${GROUP_LABELS[sidebarGroupBy]} (click to cycle)`}
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <line x1="3" y1="6" x2="21" y2="6" />
          <line x1="3" y1="12" x2="15" y2="12" />
          <line x1="3" y1="18" x2="9" y2="18" />
        </svg>
        <span>{GROUP_LABELS[sidebarGroupBy]}</span>
      </button>

      {withInbox && <Inbox variant="pane" />}

      <div className="sidebar-content">
        {!hasAnything ? (
          <div className="sidebar-empty">
            <p>No servers configured</p>
            {isMaui && <button className="btn btn-primary" onClick={() => setActivePage({ type: 'addServer' })}>Add Server</button>}
          </div>
        ) : (
          <>
            {profileGroups.map(group => (
              <ProfileSection key={group.key} group={group} />
            ))}
            {inactiveServers.length > 0 && (
              <InactiveSection servers={inactiveServers} />
            )}
          </>
        )}
      </div>

      <SidebarFooter />
    </div>
  );
}

export function SidebarFooter() {
  const setActivePage = useAppStore(s => s.setActivePage);

  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  // Close on outside click
  useEffect(() => {
    if (!menuOpen) return;
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setMenuOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [menuOpen]);

  const [theme, setThemeState] = useState(() => document.documentElement.getAttribute('data-theme') || 'dark');
  const toggleTheme = () => {
    const next = theme === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', next);
    localStorage.setItem('godmode-theme', next);
    setThemeState(next);
  };

  const openPage = (page: ActivePage) => {
    setActivePage(page);
    setMenuOpen(false);
  };

  return (
    <div className="sidebar-footer" ref={menuRef}>
      {menuOpen && (
        <div className="sidebar-footer-menu">
          <button className="sidebar-footer-menu-item" onClick={() => openPage({ type: 'appSettings' })}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <circle cx="12" cy="12" r="3" />
              <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
            </svg>
            View Settings
          </button>
          <div className="sidebar-footer-menu-sep" />
          <button className="sidebar-footer-menu-item" onClick={toggleTheme}>
            {theme === 'dark' ? (
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <circle cx="12" cy="12" r="5" /><line x1="12" y1="1" x2="12" y2="3" /><line x1="12" y1="21" x2="12" y2="23" />
                <line x1="4.22" y1="4.22" x2="5.64" y2="5.64" /><line x1="18.36" y1="18.36" x2="19.78" y2="19.78" />
                <line x1="1" y1="12" x2="3" y2="12" /><line x1="21" y1="12" x2="23" y2="12" />
                <line x1="4.22" y1="19.78" x2="5.64" y2="18.36" /><line x1="18.36" y1="5.64" x2="19.78" y2="4.22" />
              </svg>
            ) : (
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />
              </svg>
            )}
            {theme === 'dark' ? 'Light Mode' : 'Dark Mode'}
          </button>
          <button className="sidebar-footer-menu-item sidebar-logout-btn" onClick={() => {
            // Forget the API key held by this browser; the key page shows again on reload
            clearApiKey();
            window.location.href = '/';
          }}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" />
              <polyline points="16 17 21 12 16 7" />
              <line x1="21" y1="12" x2="9" y2="12" />
            </svg>
            Logout
          </button>
        </div>
      )}
      <div className="sidebar-footer-buttons">
        <button className="sidebar-settings-btn" onClick={() => setMenuOpen(!menuOpen)}>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="3" />
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
          </svg>
          Settings
        </button>
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
        <RootSection key={`${rg.serverId ?? ''}:${rg.rootName}`} rootGroup={rg} />
      ))}
    </div>
  );
}

function RootSection({ rootGroup }: { rootGroup: RootGroup }) {
  const selectedProject = useAppStore(s => s.selectedProject);
  const selectProject = useAppStore(s => s.selectProject);
  const setActivePage = useAppStore(s => s.setActivePage);
  const { serverId, rootName } = rootGroup;

  return (
    <div className="root-group">
      {!rootGroup.flat && (
        <div className="root-group-header">
          <span className="root-group-name">{rootGroup.name}</span>
          {rootGroup.actions.length > 0 && serverId && (
            <button
              className="root-action-btn"
              onClick={() => setActivePage({ type: 'createProject', context: { serverId, rootName } })}
              title="New project"
            >+</button>
          )}
        </div>
      )}
      <div className={rootGroup.flat ? 'project-list project-list-flat' : 'project-list'}>
        {rootGroup.items.length === 0 ? (
          !rootGroup.flat && <div className="project-list-empty">No projects</div>
        ) : (
          rootGroup.items.map(item => (
            <ProjectItem
              key={item.key}
              item={item}
              isSelected={
                selectedProject?.serverId === item.serverId &&
                selectedProject?.projectId === item.project.Id
              }
              onSelect={() => selectProject(item.serverId, item.project.Id)}
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
  const setActivePage = useAppStore(s => s.setActivePage);
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
                onClick={() => setActivePage({ type: 'editServer', serverId: info.Id })}
                title="Settings"
              >⚙</button>
            </div>
          </div>
        );
      })}
    </div>
  );
}
