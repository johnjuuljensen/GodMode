import { useState, useRef, useEffect, useCallback } from 'react';
import { useAppStore, type ProfileGroup, type RootGroup, type ServerConnection, type SidebarGroupBy } from '../../store';
import type { ProjectSummary } from '../../signalr/types';
import { ProjectItem } from './ProjectItem';
import { isMaui } from '../../services/hostApi';
import './Sidebar.css';

const GROUP_LABELS: Record<SidebarGroupBy, string> = {
  profile: 'Profile',
  root: 'Root',
  recent: 'Recent',
  status: 'Status',
};

export function SidebarHeader() {
  const setShowAddServer = useAppStore(s => s.setShowAddServer);
  const setShowCreateProject = useAppStore(s => s.setShowCreateProject);
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
  );
}

export function Sidebar() {
  const profileGroups = useAppStore(s => s.profileGroups);
  const inactiveServers = useAppStore(s => s.inactiveServers);
  const setShowAddServer = useAppStore(s => s.setShowAddServer);
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

      <ArchivedSection />
      <SidebarFooter />
    </div>
  );
}

function ArchivedSection() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;

  const [expanded, setExpanded] = useState(false);
  const [archived, setArchived] = useState<ProjectSummary[]>([]);
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(false);

  const loadArchived = useCallback(async () => {
    if (!hub) return;
    setLoading(true);
    try {
      setArchived(await hub.listArchivedProjects());
    } catch { /* ignore */ }
    setLoading(false);
  }, [hub]);

  useEffect(() => {
    if (expanded) loadArchived();
  }, [expanded, loadArchived]);

  const handleUnarchive = async (projectId: string) => {
    if (!hub) return;
    try {
      await hub.unarchiveProject(projectId);
      setArchived(prev => prev.filter(p => p.Id !== projectId));
    } catch (err) { console.error(err); }
  };

  const filtered = search.trim()
    ? archived.filter(p => p.Name.toLowerCase().includes(search.toLowerCase()))
    : archived;

  return (
    <div className="archived-section">
      <button className="archived-toggle" onClick={() => setExpanded(!expanded)}>
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <polyline points="21 8 21 21 3 21 3 8" /><rect x="1" y="3" width="22" height="5" /><line x1="10" y1="12" x2="14" y2="12" />
        </svg>
        <span>Archived</span>
        {archived.length > 0 && <span className="archived-count">{archived.length}</span>}
        <svg className={`archived-chevron ${expanded ? 'expanded' : ''}`} width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
          <polyline points="6 9 12 15 18 9" />
        </svg>
      </button>
      {expanded && (
        <div className="archived-content">
          <input
            className="archived-search"
            type="text"
            placeholder="Search archived..."
            value={search}
            onChange={e => setSearch(e.target.value)}
            autoFocus
          />
          {loading ? (
            <div className="archived-empty">Loading...</div>
          ) : filtered.length === 0 ? (
            <div className="archived-empty">{search ? 'No matches' : 'No archived projects'}</div>
          ) : (
            <div className="archived-list">
              {filtered.map(p => (
                <div key={p.Id} className="archived-item">
                  <div className="archived-item-info">
                    <span className="archived-item-name">{p.Name}</span>
                    {p.RootName && <span className="archived-item-root">{p.RootName}</span>}
                  </div>
                  <button className="archived-restore-btn" onClick={() => handleUnarchive(p.Id)} title="Restore">
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                      <polyline points="1 4 1 10 7 10" />
                      <path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10" />
                    </svg>
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

export function SidebarFooter() {
  const setShowMcpConfig = useAppStore(s => s.setShowMcpConfig);
  const setShowRootManager = useAppStore(s => s.setShowRootManager);
  const setShowProfileSettings = useAppStore(s => s.setShowProfileSettings);
  const setShowAppSettings = useAppStore(s => s.setShowAppSettings);
  const setShowWebhookSettings = useAppStore(s => s.setShowWebhookSettings);
  const setShowScheduleSettings = useAppStore(s => s.setShowScheduleSettings);
  const featureRoots = useAppStore(s => s.featureRoots);
  const featureMcp = useAppStore(s => s.featureMcp);
  const featureProfiles = useAppStore(s => s.featureProfiles);
  const showGodModeChat = useAppStore(s => s.showGodModeChat);
  const setShowGodModeChat = useAppStore(s => s.setShowGodModeChat);

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

  const openAndClose = (fn: (v: boolean) => void) => {
    fn(true);
    setMenuOpen(false);
  };

  return (
    <div className="sidebar-footer" ref={menuRef}>
      {menuOpen && (
        <div className="sidebar-footer-menu">
          {featureProfiles && (
            <button className="sidebar-footer-menu-item" onClick={() => openAndClose(setShowProfileSettings)}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
                <circle cx="12" cy="7" r="4" />
              </svg>
              Profiles
            </button>
          )}
          {featureRoots && (
            <button className="sidebar-footer-menu-item" onClick={() => openAndClose(setShowRootManager)}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
              </svg>
              Roots
            </button>
          )}
          {featureMcp && (
            <button className="sidebar-footer-menu-item" onClick={() => openAndClose(setShowMcpConfig)}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <rect x="2" y="2" width="20" height="8" rx="2" ry="2" /><rect x="2" y="14" width="20" height="8" rx="2" ry="2" />
                <line x1="6" y1="6" x2="6.01" y2="6" /><line x1="6" y1="18" x2="6.01" y2="18" />
              </svg>
              Connectors
            </button>
          )}
          <button className="sidebar-footer-menu-item" onClick={() => openAndClose(setShowScheduleSettings)}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <circle cx="12" cy="12" r="10" />
              <polyline points="12 6 12 12 16 14" />
            </svg>
            Schedules
          </button>
          <button className="sidebar-footer-menu-item" onClick={() => openAndClose(setShowWebhookSettings)}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
              <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
            </svg>
            Webhooks
          </button>
          <button className="sidebar-footer-menu-item" onClick={() => openAndClose(setShowAppSettings)}>
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
          <button className="sidebar-footer-menu-item sidebar-logout-btn" onClick={async () => {
            await fetch('/api/auth/logout', { method: 'POST' });
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
        <button className={`sidebar-godmode-btn ${showGodModeChat ? 'active' : ''}`} onClick={() => setShowGodModeChat(!showGodModeChat)} title="GodMode">
          <span className="sidebar-godmode-shine" />
          GodMode
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
