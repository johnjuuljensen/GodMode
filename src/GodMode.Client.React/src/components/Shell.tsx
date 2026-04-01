import { useEffect, useState } from 'react';
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
import { GodModeChat } from './GodModeChat/GodModeChat';
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
