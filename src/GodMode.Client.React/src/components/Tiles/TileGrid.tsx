import { useEffect, useRef } from 'react';
import { useAppStore, TILE_TAIL_TURNS, projectKey, isListed, type ProjectKey } from '../../store';
import { ProjectTile } from './ProjectTile';
import './TileGrid.css';

interface OpenTile {
  serverId: string;
  projectId: string;
}

export function TileGrid() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const selectedProject = useAppStore(s => s.selectedProject);
  const selectProject = useAppStore(s => s.selectProject);
  const tileMessages = useAppStore(s => s.tileMessages);
  const tileLoading = useAppStore(s => s.tileLoading);
  const subscribeTail = useAppStore(s => s.subscribeTail);
  const unsubscribeTail = useAppStore(s => s.unsubscribeTail);

  // Tail mode: only the last turns; loading ends with the server's replay-complete. A tile is open
  // while shown, and the store subscribes it again, from its offset, whenever its server reconnects.
  // When the list changes, only a tile added is opened and only a tile gone is closed: the others keep
  // their lines and their subscription (#239)
  const open = useRef(new Map<ProjectKey, OpenTile>());
  const shown = serverConnections.flatMap(conn => conn.projects.map(p => ({ serverId: conn.serverInfo.Id, projectId: p.Id })));
  const shownKeys = shown.map(t => projectKey(t.serverId, t.projectId)).join('|');
  useEffect(() => {
    const next = new Map(shown.map(t => [projectKey(t.serverId, t.projectId), t]));
    for (const [key, { serverId, projectId }] of open.current) {
      if (!next.has(key)) unsubscribeTail(serverId, projectId).catch(() => {});
    }
    for (const [key, { serverId, projectId }] of next) {
      if (!open.current.has(key)) subscribeTail(serverId, projectId, TILE_TAIL_TURNS).catch(console.error);
    }
    open.current = next;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [shownKeys]);
  useEffect(() => () => {
    for (const { serverId, projectId } of open.current.values()) unsubscribeTail(serverId, projectId).catch(() => {});
    open.current = new Map();
  }, [unsubscribeTail]);

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
