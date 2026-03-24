import { useEffect, useState, useCallback, useMemo } from 'react';
import { useAppStore } from '../store';
import { Sidebar } from './Sidebar/Sidebar';
import { ProjectView } from './Project/ProjectView';
import { TileGrid } from './Tiles/TileGrid';
import { AddServer } from './Servers/AddServer';
import { EditServer } from './Servers/EditServer';
import { CreateProject } from './Projects/CreateProject';
import { McpBrowser } from './Mcp/McpBrowser';
import { McpProfilePanel } from './Mcp/McpProfilePanel';
import { ProfileSettings } from './Profiles/ProfileSettings';
import { CreateProfile } from './Profiles/CreateProfile';
import { RootManager } from './Roots/RootManager';
import './Shell.css';

function getInitialTheme(): 'dark' | 'light' {
  const stored = localStorage.getItem('godmode-theme');
  if (stored === 'light' || stored === 'dark') return stored;
  return window.matchMedia?.('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

export function Shell() {
  const selectedProject = useAppStore(s => s.selectedProject);
  const showAddServer = useAppStore(s => s.showAddServer);
  const editServerIndex = useAppStore(s => s.editServerIndex);
  const showCreateProject = useAppStore(s => s.showCreateProject);
  const showMcpBrowser = useAppStore(s => s.showMcpBrowser);
  const showMcpProfile = useAppStore(s => s.showMcpProfile);
  const mcpProfileContext = useAppStore(s => s.mcpProfileContext);
  const showProfileSettings = useAppStore(s => s.showProfileSettings);
  const profileSettingsContext = useAppStore(s => s.profileSettingsContext);
  const setShowProfileSettings = useAppStore(s => s.setShowProfileSettings);
  const showCreateProfile = useAppStore(s => s.showCreateProfile);
  const setShowCreateProfile = useAppStore(s => s.setShowCreateProfile);
  const showRootManager = useAppStore(s => s.showRootManager);
  const setShowRootManager = useAppStore(s => s.setShowRootManager);
  const setShowAddServer = useAppStore(s => s.setShowAddServer);
  const setShowCreateProject = useAppStore(s => s.setShowCreateProject);
  const isTileView = useAppStore(s => s.isTileView);
  const setTileView = useAppStore(s => s.setTileView);
  const clearSelection = useAppStore(s => s.clearSelection);
  const totalWaitingCount = useAppStore(s => s.totalWaitingCount);
  const servers = useAppStore(s => s.servers);
  const profileFilter = useAppStore(s => s.profileFilter);
  const setProfileFilter = useAppStore(s => s.setProfileFilter);

  const allProfileNames = useMemo(() => {
    const names = new Set<string>();
    for (const server of servers) {
      if (server.connectionState !== 'connected') continue;
      for (const p of server.profiles) names.add(p.Name);
      for (const p of server.projects) {
        if (p.ProfileName) names.add(p.ProfileName);
      }
    }
    return ['All', ...Array.from(names).sort()];
  }, [servers]);

  const connectedServers = useMemo(
    () => servers.map((s, i) => ({ server: s, index: i })).filter(s => s.server.connectionState === 'connected'),
    [servers],
  );
  const hasConnectedServers = connectedServers.length > 0;
  const hasRoots = servers.some(s => s.roots.length > 0);
  const showProfileFilter = allProfileNames.length > 2; // More than just 'All' + one profile
  const realProfileNames = allProfileNames.filter(n => n !== 'All');

  // Find the active profile name for the settings gear
  // If only one profile exists, always use it (don't require selecting from dropdown)
  const activeProfileName = profileFilter !== 'All'
    ? profileFilter
    : realProfileNames.length === 1 ? realProfileNames[0] : null;

  // Find the server index for the active profile
  const profileServerIndex = useMemo(() => {
    if (!activeProfileName) return null;
    for (let i = 0; i < servers.length; i++) {
      if (servers[i].connectionState !== 'connected') continue;
      if (servers[i].profiles.some(p => p.Name === activeProfileName)) return i;
    }
    return null;
  }, [servers, activeProfileName]);

  const [theme, setTheme] = useState<'dark' | 'light'>(getInitialTheme);

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('godmode-theme', theme);
  }, [theme]);

  const toggleTheme = useCallback(() => {
    setTheme(t => t === 'dark' ? 'light' : 'dark');
  }, []);

  const isTileFullscreen = isTileView && selectedProject !== null;

  return (
    <div className="shell">
      {!isTileView && (
        <div className="shell-sidebar">
          <Sidebar />
        </div>
      )}
      <div className="shell-content">
        {/* Header bar */}
        <div className="shell-header">
          <div className="shell-header-left">
            {isTileFullscreen && (
              <button className="btn btn-secondary btn-sm" onClick={clearSelection}>
                ← Tiles
              </button>
            )}
            {hasRoots && (
              <button
                className="shell-icon-btn"
                onClick={() => setShowCreateProject(true)}
                title="Create project"
              >
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
                  <line x1="12" y1="8" x2="12" y2="14" />
                  <line x1="9" y1="11" x2="15" y2="11" />
                </svg>
              </button>
            )}
            {hasConnectedServers && (
              <button
                className="shell-icon-btn"
                onClick={() => setShowRootManager(true, servers.findIndex(s => s.connectionState === 'connected'))}
                title="Manage roots"
              >
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
                </svg>
              </button>
            )}
            <button
              className="shell-icon-btn"
              onClick={() => setShowAddServer(true)}
              title="Add server"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <rect x="2" y="2" width="20" height="8" rx="2" ry="2" />
                <rect x="2" y="14" width="20" height="8" rx="2" ry="2" />
                <line x1="6" y1="6" x2="6.01" y2="6" />
                <line x1="6" y1="18" x2="6.01" y2="18" />
              </svg>
            </button>
            {totalWaitingCount > 0 && (
              <div className="shell-badge">
                <span className="shell-badge-dot" />
                <span className="shell-badge-text">
                  {totalWaitingCount} waiting
                </span>
              </div>
            )}
          </div>
          <div className="shell-header-right">
            {showProfileFilter && (
              <select
                className="shell-profile-filter"
                value={profileFilter}
                onChange={e => setProfileFilter(e.target.value)}
              >
                {allProfileNames.map(name => (
                  <option key={name} value={name}>{name}</option>
                ))}
              </select>
            )}
            {hasConnectedServers && activeProfileName && profileServerIndex !== null && (
              <button
                className="shell-icon-btn"
                onClick={() => setShowProfileSettings(true, { serverIndex: profileServerIndex, profileName: activeProfileName })}
                title={`Settings for ${activeProfileName}`}
              >
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <circle cx="12" cy="12" r="3" />
                  <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
                </svg>
              </button>
            )}
            {hasConnectedServers && (
              <button
                className="shell-icon-btn"
                onClick={() => setShowCreateProfile(true)}
                title="Create new profile"
              >
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <line x1="12" y1="5" x2="12" y2="19" />
                  <line x1="5" y1="12" x2="19" y2="12" />
                </svg>
              </button>
            )}
            <button
              className="shell-theme-toggle"
              onClick={toggleTheme}
              title={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
            >
              {theme === 'dark' ? '☀' : '☾'}
            </button>
            <button
              className="shell-view-toggle"
              onClick={() => setTileView(!isTileView)}
              title={isTileView ? 'List view' : 'Tile view'}
            >
              {isTileView ? '☰' : '⊞'}
            </button>
          </div>
        </div>

        {/* Content area */}
        {isTileView && !isTileFullscreen ? (
          <TileGrid />
        ) : selectedProject ? (
          <ProjectView
            serverIndex={selectedProject.serverIndex}
            projectId={selectedProject.projectId}
          />
        ) : (
          <div className="shell-empty">
            <p>Select a project from the sidebar</p>
          </div>
        )}
      </div>

      {showAddServer && <AddServer />}
      {editServerIndex !== null && <EditServer index={editServerIndex} />}
      {showCreateProject && <CreateProject />}
      {showMcpBrowser && <McpBrowser />}
      {showMcpProfile && mcpProfileContext && (
        <McpProfilePanel
          serverIndex={mcpProfileContext.serverIndex}
          profileName={mcpProfileContext.profileName}
        />
      )}
      {showProfileSettings && profileSettingsContext && (
        <ProfileSettings
          serverIndex={profileSettingsContext.serverIndex}
          profileName={profileSettingsContext.profileName}
        />
      )}
      {showCreateProfile && <CreateProfile />}
      {showRootManager && <RootManager />}
    </div>
  );
}
