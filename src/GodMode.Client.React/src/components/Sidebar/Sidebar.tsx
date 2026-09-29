import { useState, useRef, useEffect } from 'react';
import { useShallow } from 'zustand/react/shallow';
import {
  useAppStore, foldItems,
  type ActivePage, type ProfileGroup, type RootGroup, type ServerConnection, type SidebarGroupBy,
} from '../../store';
import { projectKey } from '../../store/projectKey';
import { ProjectItem } from './ProjectItem';
import { Inbox } from '../Inbox/Inbox';
import './Sidebar.css';

/** What is under each profile: the profile is always the top level (#308). */
const GROUP_LABELS: Record<SidebarGroupBy, string> = {
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
  // A locked page's filter is its window's profile, fixed (#340)
  const lockedProfile = useAppStore(s => s.lockedProfile);
  const isTileView = useAppStore(s => s.isTileView);
  const setTileView = useAppStore(s => s.setTileView);
  // Tiles are a wide screen's: on a phone the toggle would only leave the inbox (#218)
  const isMobile = useAppStore(s => s.isMobile);

  const showProfileFilter = featureProfiles && lockedProfile === null && profileFilterOptions.length > 1;
  const hasRoots = serverConnections.some(c => c.roots.length > 0);

  return (
    <div className="sidebar-header">
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
        {!isMobile && (
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
        )}
        {hasRoots && (
          <button className="sidebar-add-btn" onClick={() => setActivePage({ type: 'createProject' })} title="Create project">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
              <line x1="12" y1="8" x2="12" y2="14" /><line x1="9" y1="11" x2="15" y2="11" />
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

/**
 * inbox: 'pane' puts the needs-you pane above the project list (wide screens); 'screen' shows the inbox in
 * place of the list (the phone's home). Either way the inbox has one place in the tree, so crossing the
 * phone breakpoint re-renders it rather than remounting it (#240).
 */
export function Sidebar({ inbox }: { inbox?: 'pane' | 'screen' }) {
  const profileGroups = useAppStore(s => s.profileGroups);
  const inactiveServers = useAppStore(s => s.inactiveServers);
  const setActivePage = useAppStore(s => s.setActivePage);
  const sidebarGroupBy = useAppStore(s => s.sidebarGroupBy);
  const cycleSidebarGroupBy = useAppStore(s => s.cycleSidebarGroupBy);

  const hasAnything = profileGroups.length > 0 || inactiveServers.length > 0;
  const showsList = inbox !== 'screen';

  return (
    <div className="sidebar">
      <SidebarHeader />

      {showsList && (
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
      )}

      {inbox && <Inbox variant={inbox} />}

      {showsList && (
        <div className="sidebar-content">
          {!hasAnything ? (
            <div className="sidebar-empty">
              <p>No servers configured</p>
              <button className="btn btn-primary" onClick={() => setActivePage({ type: 'addServer' })}>Add Server</button>
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
      )}

      {showsList && <SidebarFooter />}
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
          {/* Rare, so here rather than in the header (#311); the empty list keeps its own */}
          <button className="sidebar-footer-menu-item" onClick={() => openPage({ type: 'addServer' })}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="2" y="2" width="20" height="8" rx="2" ry="2" />
              <rect x="2" y="14" width="20" height="8" rx="2" ry="2" />
              <line x1="6" y1="6" x2="6.01" y2="6" /><line x1="6" y1="18" x2="6.01" y2="18" />
            </svg>
            Add server
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
  // Where the app has windows (Windows), and only in the main window: a locked one is the profile's already (#340)
  const canOpenWindow = useAppStore(s => s.canOpenWindows && s.lockedProfile === null);
  const openProfileWindow = useAppStore(s => s.openProfileWindow);
  return (
    <div className="profile-group">
      <div className="profile-group-header">
        <span className="profile-group-name">{group.name}</span>
        <span className="profile-group-meta">
          {canOpenWindow && (
            <button
              className="profile-window-btn"
              onClick={() => openProfileWindow(group.name).catch(console.error)}
              title="Open in its own window"
              aria-label={`Open ${group.name} in its own window`}
            >
              <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M14 3h7v7" /><line x1="21" y1="3" x2="11" y2="13" />
                <path d="M19 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2h5" />
              </svg>
            </button>
          )}
          <span className="profile-group-count">{group.projectCount}</span>
        </span>
      </div>
      {group.rootGroups.map(rg => (
        <RootSection key={`${rg.serverId ?? ''}:${rg.rootName}`} rootGroup={rg} />
      ))}
    </div>
  );
}

/** How often the list looks again at which sessions have gone quiet long enough to fold. */
const FOLD_TICK_MS = 60_000;

/** The time now, again every `everyMs`. */
function useNow(everyMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), everyMs);
    return () => clearInterval(timer);
  }, [everyMs]);
  return now;
}

function RootSection({ rootGroup }: { rootGroup: RootGroup }) {
  const selectedProject = useAppStore(s => s.selectedProject);
  const selectProject = useAppStore(s => s.selectProject);
  const setActivePage = useAppStore(s => s.setActivePage);
  const attention = useAppStore(s => s.attention);
  const projectQuestions = useAppStore(s => s.projectQuestions);
  const [showOlder, setShowOlder] = useState(false);
  const now = useNow(FOLD_TICK_MS);
  const { serverId, profileName, rootName } = rootGroup;

  // Older sessions fold under "N older", one tap away (#325): never one that needs the user or is open
  const needsYou = new Set(attention.map(a => projectKey(a.serverId, a.ProjectId)));
  const { shown, older } = foldItems(rootGroup.items, now, item =>
    needsYou.has(item.key) || !!projectQuestions[item.key]
    || (selectedProject?.serverId === item.serverId && selectedProject.projectId === item.project.Id));
  const listed = showOlder ? [...shown, ...older] : shown;

  return (
    <div className="root-group">
      {!rootGroup.flat && (
        <div className="root-group-header">
          <span className="root-group-name">{rootGroup.name}</span>
          {rootGroup.canCreate && serverId && (
            <button
              className="root-action-btn"
              onClick={() => setActivePage({ type: 'createProject', context: { serverId, profileName, rootName } })}
              title="New project"
            >+</button>
          )}
        </div>
      )}
      <div className={rootGroup.flat ? 'project-list project-list-flat' : 'project-list'}>
        {rootGroup.items.length === 0 ? (
          !rootGroup.flat && <div className="project-list-empty">No projects</div>
        ) : (
          listed.map(item => (
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
        {older.length > 0 && (
          <button className="project-list-older" onClick={() => setShowOlder(!showOlder)} aria-expanded={showOlder}>
            {showOlder ? 'Hide older' : `${older.length} older`}
          </button>
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
