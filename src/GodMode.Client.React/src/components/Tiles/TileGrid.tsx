import { useEffect, useRef } from 'react';
import { useAppStore, TILE_TAIL_TURNS, projectKey } from '../../store';
import { ProjectTile } from './ProjectTile';
import './TileGrid.css';

export function TileGrid() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const selectedProject = useAppStore(s => s.selectedProject);
  const selectProject = useAppStore(s => s.selectProject);
  const tileMessages = useAppStore(s => s.tileMessages);
  const tileLoading = useAppStore(s => s.tileLoading);
  const subscribeTail = useAppStore(s => s.subscribeTail);
  const clearTileMessages = useAppStore(s => s.clearTileMessages);

  const subscribedRef = useRef(new Set<string>());

  useEffect(() => {
    clearTileMessages();
    const toSubscribe: { serverId: string; projectId: string }[] = [];

    for (const conn of serverConnections) {
      if (conn.connectionState !== 'connected') continue;
      for (const project of conn.projects) {
        if (!subscribedRef.current.has(projectKey(conn.serverInfo.Id, project.Id))) {
          toSubscribe.push({ serverId: conn.serverInfo.Id, projectId: project.Id });
        }
      }
    }

    const newSubscribed = new Set<string>();
    for (const { serverId, projectId } of toSubscribe) {
      newSubscribed.add(projectKey(serverId, projectId));
      // Tail mode: only the last turns; loading ends with the server's replay-complete
      subscribeTail(serverId, projectId, TILE_TAIL_TURNS).catch(console.error);
    }

    for (const conn of serverConnections) {
      if (conn.connectionState !== 'connected') continue;
      for (const p of conn.projects) newSubscribed.add(projectKey(conn.serverInfo.Id, p.Id));
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
            key={projectKey(serverId, project.Id)}
            project={project}
            serverId={serverId}
            messages={tileMessages[projectKey(serverId, project.Id)] ?? []}
            isLoading={tileLoading[projectKey(serverId, project.Id)] ?? false}
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
