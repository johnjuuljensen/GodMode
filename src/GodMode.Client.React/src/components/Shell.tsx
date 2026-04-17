import { useEffect, useRef, useState } from 'react';
import { useAppStore, type ActivePage } from '../store';
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
import { ScheduleSettings } from './Schedules/ScheduleSettings';
import { StorageBrowser } from './Storage/StorageBrowser';
import { GodModeChat } from './GodModeChat/GodModeChat';
import { CONNECTOR_CATALOG } from '../connectors-catalog';
import type { McpServerConfig } from '../signalr/types';
import './Shell.css';

function getInitialTheme(): 'dark' | 'light' {
  const stored = localStorage.getItem('godmode-theme');
  if (stored === 'light' || stored === 'dark') return stored;
  return window.matchMedia?.('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

function PageContent({ page }: { page: ActivePage }) {
  const closePage = useAppStore(s => s.closePage);
  return (
    <div className="page-view">
      <div className="page-back-bar">
        <button className="btn btn-secondary btn-sm" onClick={closePage}>← Back</button>
      </div>
      <div className="page-body">
        {page.type === 'mcpConfig' && <McpConfigPanel />}
        {page.type === 'rootManager' && <RootManager />}
        {page.type === 'profileSettings' && <ProfileSettings />}
        {page.type === 'appSettings' && <AppSettings />}
        {page.type === 'webhookSettings' && <WebhookSettings />}
        {page.type === 'scheduleSettings' && <ScheduleSettings />}
        {page.type === 'storageBrowser' && <StorageBrowser />}
        {page.type === 'addServer' && <AddServer />}
        {page.type === 'editServer' && <EditServer serverId={page.serverId} />}
        {page.type === 'createProject' && <CreateProject />}
      </div>
    </div>
  );
}

export function Shell() {
  const selectedProject = useAppStore(s => s.selectedProject);
  const isTileView = useAppStore(s => s.isTileView);
  const clearSelection = useAppStore(s => s.clearSelection);
  const showGodModeChat = useAppStore(s => s.showGodModeChat);
  const activePage = useAppStore(s => s.activePage);
  const isMobile = useAppStore(s => s.isMobile);
  const setIsMobile = useAppStore(s => s.setIsMobile);

  const [theme] = useState<'dark' | 'light'>(getInitialTheme);

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('godmode-theme', theme);
  }, [theme]);

  // Mobile detection
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 768px)');
    setIsMobile(mq.matches);
    const handler = (e: MediaQueryListEvent) => setIsMobile(e.matches);
    mq.addEventListener('change', handler);
    return () => mq.removeEventListener('change', handler);
  }, [setIsMobile]);

  // Handle OAuth connector redirect: auto-add the pending MCP connector
  const oauthHandled = useRef(false);
  const serverConnections = useAppStore(s => s.serverConnections);
  const setActivePage = useAppStore(s => s.setActivePage);
  useEffect(() => {
    if (oauthHandled.current) return;
    const params = new URLSearchParams(window.location.search);
    const oauthSuccess = params.get('oauthSuccess');
    if (!oauthSuccess) return;

    const conn = serverConnections.find(c => c.connectionState === 'connected');
    if (!conn?.hub) return;

    oauthHandled.current = true;
    window.history.replaceState({}, '', window.location.pathname);

    const raw = sessionStorage.getItem('oauth-pending-connector');
    sessionStorage.removeItem('oauth-pending-connector');

    // Navigate to connectors page so user sees the result
    setActivePage({ type: 'mcpConfig' });

    if (!raw) return;

    try {
      const { connectorId, profileName } = JSON.parse(raw) as { connectorId: string; profileName: string };
      const catalog = CONNECTOR_CATALOG.find(c => c.id === connectorId);
      if (!catalog?.config.url) return;

      const config: McpServerConfig = { Url: catalog.config.url };
      conn.hub.addMcpServer(catalog.id, config, 'profile', profileName)
        .then(() => console.info(`[oauth] Auto-added ${catalog.id} to profile ${profileName}`))
        .catch(e => console.error('[oauth] Failed to auto-add connector:', e));
    } catch { /* ignore */ }
  }, [serverConnections, setActivePage]);

  const isTileFullscreen = isTileView && selectedProject !== null;

  // ── Mobile layout ──
  if (isMobile) {
    return (
      <div className="shell shell-mobile">
        {activePage ? (
          <PageContent page={activePage} />
        ) : selectedProject ? (
          <div className="shell-mobile-project">
            <div className="page-back-bar">
              <button className="btn btn-secondary btn-sm" onClick={clearSelection}>← Back</button>
            </div>
            <ProjectView serverId={selectedProject.serverId} projectId={selectedProject.projectId} />
          </div>
        ) : showGodModeChat ? (
          <GodModeChat />
        ) : isTileView ? (
          <div className="shell-mobile-tiles">
            <SidebarHeader />
            <TileGrid />
            <SidebarFooter />
          </div>
        ) : (
          <Sidebar />
        )}
      </div>
    );
  }

  // ── Desktop layout ──
  return (
    <div className={`shell ${isTileView ? 'shell-tile-mode' : ''}`}>
      {!isTileView ? (
        <>
          <div className="shell-sidebar">
            <Sidebar />
          </div>
          <div className="shell-content">
            {activePage ? (
              <PageContent page={activePage} />
            ) : showGodModeChat ? (
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
            {activePage ? (
              <PageContent page={activePage} />
            ) : (
              <>
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
              </>
            )}
          </div>
          <SidebarFooter />
        </>
      )}
    </div>
  );
}
