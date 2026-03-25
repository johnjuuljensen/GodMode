import { useEffect, useRef } from 'react';
import { useAppStore } from '../../store';
import { ProjectTile } from './ProjectTile';
import './TileGrid.css';

export function TileGrid() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const selectedProject = useAppStore(s => s.selectedProject);
  const selectProject = useAppStore(s => s.selectProject);
  const tileMessages = useAppStore(s => s.tileMessages);
  const tileLoading = useAppStore(s => s.tileLoading);
  const setTileLoading = useAppStore(s => s.setTileLoading);
  const clearTileMessages = useAppStore(s => s.clearTileMessages);

  const subscribedRef = useRef(new Set<string>());

  useEffect(() => {
    clearTileMessages();
    const toSubscribe: { hub: typeof serverConnections[0]['hub']; projectId: string }[] = [];

    for (const conn of serverConnections) {
      if (conn.connectionState !== 'connected') continue;
      for (const project of conn.projects) {
        if (!subscribedRef.current.has(project.Id)) {
          toSubscribe.push({ hub: conn.hub, projectId: project.Id });
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

    for (const conn of serverConnections) {
      if (conn.connectionState !== 'connected') continue;
      for (const p of conn.projects) newSubscribed.add(p.Id);
    }
    subscribedRef.current = newSubscribed;

    return () => {
      for (const conn of serverConnections) {
        if (conn.connectionState !== 'connected') continue;
        for (const project of conn.projects) {
          conn.hub.unsubscribeProject(project.Id).catch(() => {});
        }
      }
      subscribedRef.current.clear();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serverConnections.map(s => `${s.connectionState}:${s.projects.map(p => p.Id).join(',')}`).join('|')]);

  const hasConnected = serverConnections.some(s => s.connectionState === 'connected');
  const allProjects = serverConnections.flatMap(conn =>
    conn.connectionState === 'connected'
      ? conn.projects.map(p => ({ serverId: conn.serverInfo.Id, project: p }))
      : []
  );

  if (!hasConnected) {
    return <div className="tile-grid-empty">No connected servers</div>;
  }

  if (allProjects.length === 0) {
    return <div className="tile-grid-empty">No projects</div>;
  }

  return (
    <div className="tile-grid-scroll">
      <div className="tile-grid">
        {allProjects.map(({ serverId, project }) => (
          <ProjectTile
            key={project.Id}
            project={project}
            messages={tileMessages[project.Id] ?? []}
            isLoading={tileLoading[project.Id] ?? false}
            isSelected={
              selectedProject?.serverId === serverId &&
              selectedProject?.projectId === project.Id
            }
            onSelect={() => selectProject(serverId, project.Id)}
          />
        ))}
      </div>
    </div>
  );
}
