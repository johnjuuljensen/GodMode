import { useEffect, useState, useCallback, useMemo } from 'react';
import { useAppStore } from '../store';
import { Sidebar } from './Sidebar/Sidebar';
import { ProjectView } from './Project/ProjectView';
import { TileGrid } from './Tiles/TileGrid';
import { AddServer } from './Servers/AddServer';
import { EditServer } from './Servers/EditServer';
import { CreateProject } from './Projects/CreateProject';
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

  const showProfileFilter = allProfileNames.length > 1;

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
    </div>
  );
}
