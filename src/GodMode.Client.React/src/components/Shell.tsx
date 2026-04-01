import { useEffect, useState } from 'react';
import { useAppStore } from '../store';
import { Sidebar, SidebarHeader, SidebarFooter } from './Sidebar/Sidebar';
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
  const clearSelection = useAppStore(s => s.clearSelection);
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
    <div className={`shell ${isTileView ? 'shell-tile-mode' : ''}`}>
      {!isTileView ? (
        <>
          <div className="shell-sidebar">
            <Sidebar />
          </div>
          <div className="shell-content">
            {showGodModeChat ? (
              <GodModeChat />
            ) : selectedProject ? (
              <ProjectView serverId={selectedProject.serverId} projectId={selectedProject.projectId} />
            ) : (
              <div className="shell-empty"><p>Select a project from the sidebar</p></div>
            )}
          </div>
        </>
      ) : (
        <>
          <SidebarHeader />
          <div className="shell-content">
            {isTileFullscreen && (
              <div className="shell-back-bar">
                <button className="btn btn-secondary btn-sm" onClick={clearSelection}>← Tiles</button>
              </div>
            )}
            {showGodModeChat ? (
              <GodModeChat />
            ) : isTileFullscreen ? (
              <ProjectView serverId={selectedProject!.serverId} projectId={selectedProject!.projectId} />
            ) : (
              <TileGrid />
            )}
          </div>
          <SidebarFooter />
        </>
      )}

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
