import { useEffect, useRef } from 'react';
import { useAppStore } from '../../store';
import { ProjectTile } from './ProjectTile';
import './TileGrid.css';

export function TileGrid() {
  const servers = useAppStore(s => s.servers);
  const selectedProject = useAppStore(s => s.selectedProject);
  const selectProject = useAppStore(s => s.selectProject);
  const tileMessages = useAppStore(s => s.tileMessages);
  const tileLoading = useAppStore(s => s.tileLoading);
  const setTileLoading = useAppStore(s => s.setTileLoading);
  const clearTileMessages = useAppStore(s => s.clearTileMessages);

  const subscribedRef = useRef(new Set<string>());

  useEffect(() => {
    clearTileMessages();
    const toSubscribe: { hub: typeof servers[0]['hub']; projectId: string }[] = [];

    for (const server of servers) {
      if (server.connectionState !== 'connected') continue;
      for (const project of server.projects) {
        if (!subscribedRef.current.has(project.Id)) {
          toSubscribe.push({ hub: server.hub, projectId: project.Id });
        }
      }
    }

    const newSubscribed = new Set<string>();
    for (const { hub, projectId } of toSubscribe) {
      newSubscribed.add(projectId);
      setTileLoading(projectId, true);
      hub.subscribeProject(projectId, 0).catch(console.error);
      setTimeout(() => setTileLoading(projectId, false), 2000);
    }

    // Also keep existing subscriptions that are still valid
    for (const server of servers) {
      if (server.connectionState !== 'connected') continue;
      for (const p of server.projects) newSubscribed.add(p.Id);
    }
    subscribedRef.current = newSubscribed;

    return () => {
      // Unsubscribe all on unmount
      for (const server of servers) {
        if (server.connectionState !== 'connected') continue;
        for (const project of server.projects) {
          server.hub.unsubscribeProject(project.Id).catch(() => {});
        }
      }
      subscribedRef.current.clear();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [servers.map(s => `${s.connectionState}:${s.projects.map(p => p.Id).join(',')}`).join('|')]);

  const hasConnected = servers.some(s => s.connectionState === 'connected');
  const allProjects = servers.flatMap((server, si) =>
    server.connectionState === 'connected'
      ? server.projects.map(p => ({ serverIndex: si, project: p }))
      : []
  );

  if (!hasConnected) {
    return (
      <div className="tile-grid-empty">
        No connected servers
      </div>
    );
  }

  if (allProjects.length === 0) {
    return (
      <div className="tile-grid-empty">
        No projects
      </div>
    );
  }

  return (
    <div className="tile-grid-scroll">
      <div className="tile-grid">
        {allProjects.map(({ serverIndex, project }) => (
          <ProjectTile
            key={project.Id}
            project={project}
            messages={tileMessages[project.Id] ?? []}
            isLoading={tileLoading[project.Id] ?? false}
            isSelected={
              selectedProject?.serverIndex === serverIndex &&
              selectedProject?.projectId === project.Id
            }
            onSelect={() => selectProject(serverIndex, project.Id)}
          />
        ))}
      </div>
    </div>
  );
}
