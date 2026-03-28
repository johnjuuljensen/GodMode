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
  const setShowAddServer = useAppStore(s => s.setShowAddServer);
  const setShowCreateProject = useAppStore(s => s.setShowCreateProject);
  const isTileView = useAppStore(s => s.isTileView);
  const setTileView = useAppStore(s => s.setTileView);
  const clearSelection = useAppStore(s => s.clearSelection);
  const totalWaitingCount = useAppStore(s => s.totalWaitingCount);
  const servers = useAppStore(s => s.servers);
  const profileFilter = useAppStore(s => s.profileFilter);
  const setProfileFilter = useAppStore(s => s.setProfileFilter);
  const showMcpBrowser = useAppStore(s => s.showMcpBrowser);
  const showMcpProfile = useAppStore(s => s.showMcpProfile);
  const mcpProfileContext = useAppStore(s => s.mcpProfileContext);
  const showProfileSettings = useAppStore(s => s.showProfileSettings);
  const profileSettingsContext = useAppStore(s => s.profileSettingsContext);
  const showCreateProfile = useAppStore(s => s.showCreateProfile);

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

  const hasRoots = servers.some(s => s.connectionState === 'connected' && s.roots.length > 0);
  const showProfileFilter = allProfileNames.length > 2;

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
        <McpProfilePanel serverIndex={mcpProfileContext.serverIndex} profileName={mcpProfileContext.profileName} />
      )}
      {showProfileSettings && profileSettingsContext && (
        <ProfileSettings serverIndex={profileSettingsContext.serverIndex} profileName={profileSettingsContext.profileName} />
      )}
      {showCreateProfile && <CreateProfile />}
    </div>
  );
}
