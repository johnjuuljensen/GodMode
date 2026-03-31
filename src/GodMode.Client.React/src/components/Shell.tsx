import { useEffect, useState, useCallback } from 'react';
import { useAppStore } from '../store';
import { Sidebar } from './Sidebar/Sidebar';
import { ProjectView } from './Project/ProjectView';
import { TileGrid } from './Tiles/TileGrid';
import { AddServer } from './Servers/AddServer';
import { EditServer } from './Servers/EditServer';
import { CreateProject } from './Projects/CreateProject';
import { McpConfigPanel } from './Mcp/McpConfigPanel';
import { RootManager } from './Roots/RootManager';
import { ProfileSettings } from './Profiles/ProfileSettings';
import { AppSettings } from './AppSettings';
import { isMaui, openDevTools } from '../services/hostApi';
import './Shell.css';

function getInitialTheme(): 'dark' | 'light' {
  const stored = localStorage.getItem('godmode-theme');
  if (stored === 'light' || stored === 'dark') return stored;
  return window.matchMedia?.('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

export function Shell() {
  const selectedProject = useAppStore(s => s.selectedProject);
  const showAddServer = useAppStore(s => s.showAddServer);
  const editServerId = useAppStore(s => s.editServerId);
  const showCreateProject = useAppStore(s => s.showCreateProject);
  const isTileView = useAppStore(s => s.isTileView);
  const setTileView = useAppStore(s => s.setTileView);
  const clearSelection = useAppStore(s => s.clearSelection);
  const totalWaitingCount = useAppStore(s => s.totalWaitingCount);
  const profileFilter = useAppStore(s => s.profileFilter);
  const setProfileFilter = useAppStore(s => s.setProfileFilter);
  const profileFilterOptions = useAppStore(s => s.profileFilterOptions);
  const showMcpConfig = useAppStore(s => s.showMcpConfig);
  const setShowMcpConfig = useAppStore(s => s.setShowMcpConfig);
  const showRootManager = useAppStore(s => s.showRootManager);
  const setShowRootManager = useAppStore(s => s.setShowRootManager);
  const showProfileSettings = useAppStore(s => s.showProfileSettings);
  const setShowProfileSettings = useAppStore(s => s.setShowProfileSettings);
  const showAppSettings = useAppStore(s => s.showAppSettings);
  const setShowAppSettings = useAppStore(s => s.setShowAppSettings);
  const featureRoots = useAppStore(s => s.featureRoots);
  const featureMcp = useAppStore(s => s.featureMcp);
  const featureProfiles = useAppStore(s => s.featureProfiles);

  const showProfileFilter = featureProfiles && profileFilterOptions.length > 1;

  const [theme, setTheme] = useState<'dark' | 'light'>(getInitialTheme);

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('godmode-theme', theme);
  }, [theme]);

  const toggleTheme = useCallback(() => setTheme(t => t === 'dark' ? 'light' : 'dark'), []);

  const isTileFullscreen = isTileView && selectedProject !== null;

  return (
    <div className="shell">
      {!isTileView && (
        <div className="shell-sidebar">
          <Sidebar />
        </div>
      )}
      <div className="shell-content">
        <div className="shell-header">
          <div className="shell-header-left">
            {isTileFullscreen && (
              <button className="btn btn-secondary btn-sm" onClick={clearSelection}>← Tiles</button>
            )}
            {totalWaitingCount > 0 && (
              <div className="shell-badge">
                <span className="shell-badge-dot" />
                <span className="shell-badge-text">{totalWaitingCount} waiting</span>
              </div>
            )}
          </div>
          <div className="shell-header-right">
            {showProfileFilter && (
              <select className="shell-profile-filter" value={profileFilter} onChange={e => setProfileFilter(e.target.value)}>
                {profileFilterOptions.map(name => <option key={name} value={name}>{name}</option>)}
              </select>
            )}
            {featureMcp && (
              <button className="shell-view-toggle" onClick={() => setShowMcpConfig(true)} title="MCP Servers">
                MCP
              </button>
            )}
            {featureRoots && (
              <button className="shell-view-toggle" onClick={() => setShowRootManager(true)} title="Root Manager">
                Roots
              </button>
            )}
            {featureProfiles && (
              <button className="shell-view-toggle" onClick={() => setShowProfileSettings(true)} title="Profile Settings">
                Profiles
              </button>
            )}
            <button className="shell-view-toggle" onClick={() => setShowAppSettings(true)} title="Settings">
              {'⚙'}
            </button>
            <button className="shell-theme-toggle" onClick={toggleTheme} title={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}>
              {theme === 'dark' ? '☀' : '☾'}
            </button>
            <button className="shell-view-toggle" onClick={() => setTileView(!isTileView)} title={isTileView ? 'List view' : 'Tile view'}>
              {isTileView ? '☰' : '⊞'}
            </button>
            {isMaui && (
              <button className="shell-view-toggle" onClick={() => openDevTools()} title="Open DevTools">
                {'{ }'}
              </button>
            )}
          </div>
        </div>

        {isTileView && !isTileFullscreen ? (
          <TileGrid />
        ) : selectedProject ? (
          <ProjectView serverId={selectedProject.serverId} projectId={selectedProject.projectId} />
        ) : (
          <div className="shell-empty"><p>Select a project from the sidebar</p></div>
        )}
      </div>

      {showAddServer && <AddServer />}
      {editServerId !== null && <EditServer serverId={editServerId} />}
      {showCreateProject && <CreateProject />}
      {showMcpConfig && <McpConfigPanel />}
      {showRootManager && <RootManager />}
      {showProfileSettings && <ProfileSettings />}
      {showAppSettings && <AppSettings />}
    </div>
  );
}
