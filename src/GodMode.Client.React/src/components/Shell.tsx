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
import { ConfirmDialog } from './ConfirmDialog';
import { Inbox, HomeTabBar } from './Inbox/Inbox';
import { useAttentionTitle } from './Inbox/useAttentionTitle';
import { goBack, useHashRoute } from '../routing';
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
        <button className="btn btn-secondary btn-sm" onClick={() => goBack(closePage)}>← Back</button>
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
  const homeView = useAppStore(s => s.homeView);
  const setHomeView = useAppStore(s => s.setHomeView);

  const [theme] = useState<'dark' | 'light'>(getInitialTheme);

  // Each screen is a history entry, so browser and Android back walk back through them
  useHashRoute();
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

  // One tree for every layout: the slots below keep their positions whichever layout shows,
  // so crossing the phone breakpoint re-renders a page or a project instead of remounting it
  // (which would drop a half-filled form or a typed reply). A slot a layout lacks renders null.
  const showsPage = activePage !== null;
  const project = showsPage ? null : selectedProject;
  const showsTiles = !showsPage && !project && isTileView;
  // On a phone home is the inbox, or the project list, with nothing beside it
  const phoneHome = isMobile && !showsPage && !project && !isTileView;

  const sidebarSlot = isTileView
    ? (!isMobile || showsTiles) && <SidebarHeader />
    : !isMobile ? <div className="shell-sidebar"><Sidebar withInbox /></div>
    : phoneHome && (
      <div className="shell-sidebar shell-mobile-home">
        {homeView === 'inbox' ? <><SidebarHeader /><Inbox variant="screen" /></> : <Sidebar />}
        <HomeTabBar tab={homeView} onChange={setHomeView} />
      </div>
    );
  const footerSlot = isTileView && (!isMobile || showsTiles) && <SidebarFooter />;
  const backBar = project && (isMobile || isTileView) && (
    <div className={isMobile ? 'page-back-bar' : 'shell-back-bar'}>
      <button className="btn btn-secondary btn-sm" onClick={() => goBack(clearSelection)}>{isMobile ? '← Back' : '← Tiles'}</button>
    </div>
  );

  const shellClass = ['shell', isMobile && 'shell-mobile', isTileView && 'shell-tile-mode'].filter(Boolean).join(' ');
  const contentClass = ['shell-content', isMobile && project && 'shell-mobile-project'].filter(Boolean).join(' ');

  return (
    <div className={shellClass}>
      {sidebarSlot}
      {!phoneHome && (
        <div className={contentClass}>
          {backBar}
          {activePage && <PageContent page={activePage} />}
          {project && <ProjectView serverId={project.serverId} projectId={project.projectId} />}
          {showsTiles && !isMobile && <Inbox variant="pane" />}
          {showsTiles && <TileGrid />}
          {!isMobile && !isTileView && !showsPage && !project && (
            <div className="shell-empty"><p>Select a project from the sidebar</p></div>
          )}
        </div>
      )}
      {footerSlot}
      <ConfirmDialog />
    </div>
  );
}
