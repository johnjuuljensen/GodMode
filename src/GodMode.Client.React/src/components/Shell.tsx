import { useEffect, useState } from 'react';
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
import { WebhookSettings } from './Webhooks/WebhookSettings';
import { GodModeChat } from './GodModeChat/GodModeChat';
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
  const showMcpConfig = useAppStore(s => s.showMcpConfig);
  const showRootManager = useAppStore(s => s.showRootManager);
  const showProfileSettings = useAppStore(s => s.showProfileSettings);
  const showAppSettings = useAppStore(s => s.showAppSettings);
  const showWebhookSettings = useAppStore(s => s.showWebhookSettings);
  const showGodModeChat = useAppStore(s => s.showGodModeChat);

  const [theme] = useState<'dark' | 'light'>(getInitialTheme);

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('godmode-theme', theme);
  }, [theme]);

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

        {showGodModeChat ? (
          <GodModeChat />
        ) : isTileView && !isTileFullscreen ? (
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
      {showWebhookSettings && <WebhookSettings />}
    </div>
  );
}
