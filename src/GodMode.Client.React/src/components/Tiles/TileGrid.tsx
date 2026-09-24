import { useEffect } from 'react';
import { useAppStore, TILE_TAIL_TURNS, projectKey, isListed } from '../../store';
import { ProjectTile } from './ProjectTile';
import './TileGrid.css';

export function TileGrid() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const selectedProject = useAppStore(s => s.selectedProject);
  const selectProject = useAppStore(s => s.selectProject);
  const tileMessages = useAppStore(s => s.tileMessages);
  const tileLoading = useAppStore(s => s.tileLoading);
  const subscribeTail = useAppStore(s => s.subscribeTail);
  const unsubscribeTail = useAppStore(s => s.unsubscribeTail);
  const clearTileMessages = useAppStore(s => s.clearTileMessages);

  useEffect(() => {
    clearTileMessages();
    const tiles = serverConnections.flatMap(conn => conn.projects.map(p => ({ serverId: conn.serverInfo.Id, projectId: p.Id })));
    // Tail mode: only the last turns; loading ends with the server's replay-complete. A tile is open
    // while shown, and the store subscribes it again, from its offset, whenever its server reconnects
    for (const { serverId, projectId } of tiles) subscribeTail(serverId, projectId, TILE_TAIL_TURNS).catch(console.error);
    return () => {
      for (const { serverId, projectId } of tiles) unsubscribeTail(serverId, projectId).catch(() => {});
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serverConnections.map(s => `${s.serverInfo.Id}:${s.projects.map(p => p.Id).join(',')}`).join('|')]);

  // A reconnecting server's tiles stay, as they were until it is back
  const listed = serverConnections.filter(isListed);
  const allProjects = listed.flatMap(conn => conn.projects.map(p => ({ serverId: conn.serverInfo.Id, project: p })));

  if (listed.length === 0) {
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
