import { useEffect, useState } from 'react';
import { useAppStore, type ActivePage } from '../store';
import { Sidebar, SidebarHeader, SidebarFooter } from './Sidebar/Sidebar';
import { ProjectView } from './Project/ProjectView';
import { TileGrid } from './Tiles/TileGrid';
import { AddServer } from './Servers/AddServer';
import { EditServer } from './Servers/EditServer';
import { CreateProject } from './Projects/CreateProject';
import { ProfileSettings } from './Profiles/ProfileSettings';
import { AppSettings } from './AppSettings';
import { Inbox, HomeTabBar, type HomeTab } from './Inbox/Inbox';
import { useAttentionTitle } from './Inbox/useAttentionTitle';
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
        {page.type === 'profileSettings' && <ProfileSettings />}
        {page.type === 'appSettings' && <AppSettings />}
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
  const activePage = useAppStore(s => s.activePage);
  const isMobile = useAppStore(s => s.isMobile);
  const setIsMobile = useAppStore(s => s.setIsMobile);

  const [theme] = useState<'dark' | 'light'>(getInitialTheme);
  // The phone's home: the inbox, or the project list
  const [homeTab, setHomeTab] = useState<HomeTab>('inbox');
  useAttentionTitle();

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
        ) : isTileView ? (
          <div className="shell-mobile-tiles">
            <SidebarHeader />
            <TileGrid />
            <SidebarFooter />
          </div>
        ) : (
          <div className="shell-mobile-home">
            {homeTab === 'inbox' ? <><SidebarHeader /><Inbox variant="screen" /></> : <Sidebar />}
            <HomeTabBar tab={homeTab} onChange={setHomeTab} />
          </div>
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
            <Sidebar withInbox />
          </div>
          <div className="shell-content">
            {activePage ? (
              <PageContent page={activePage} />
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
                {isTileFullscreen ? (
                  <ProjectView serverId={selectedProject!.serverId} projectId={selectedProject!.projectId} />
                ) : (
                  <>
                    <Inbox variant="pane" />
                    <TileGrid />
                  </>
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
