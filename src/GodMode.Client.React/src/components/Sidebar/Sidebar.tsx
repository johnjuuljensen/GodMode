import { useAppStore, type ServerState } from '../../store';
import { ServerItem } from './ServerItem';
import { ProjectItem } from './ProjectItem';
import './Sidebar.css';

export function Sidebar() {
  const servers = useAppStore(s => s.servers);
  const setShowAddServer = useAppStore(s => s.setShowAddServer);

  return (
    <div className="sidebar">
      <div className="sidebar-header">
        <span className="sidebar-title">GodMode</span>
        <button
          className="sidebar-add-btn"
          onClick={() => setShowAddServer(true)}
          title="Add server"
        >
          +
        </button>
      </div>
      <div className="sidebar-content">
        {servers.length === 0 ? (
          <div className="sidebar-empty">
            <p>No servers configured</p>
            <button
              className="btn btn-primary"
              onClick={() => setShowAddServer(true)}
            >
              Add Server
            </button>
          </div>
        ) : (
          servers.map((server, index) => (
            <ServerSection key={index} server={server} index={index} />
          ))
        )}
      </div>
    </div>
  );
}

function ServerSection({ server, index }: { server: ServerState; index: number }) {
  const connectServer = useAppStore(s => s.connectServer);
  const disconnectServer = useAppStore(s => s.disconnectServer);
  const selectProject = useAppStore(s => s.selectProject);
  const selectedProject = useAppStore(s => s.selectedProject);
  const setEditServerIndex = useAppStore(s => s.setEditServerIndex);

  return (
    <div className="server-section">
      <ServerItem
        server={server}
        onConnect={() => connectServer(index)}
        onDisconnect={() => disconnectServer(index)}
        onEdit={() => setEditServerIndex(index)}
      />
      {server.connectionState === 'connected' && (
        <div className="project-list">
          {server.projects.length === 0 ? (
            <div className="project-list-empty">No projects</div>
          ) : (
            server.projects.map(project => (
              <ProjectItem
                key={project.Id}
                project={project}
                isSelected={
                  selectedProject?.serverIndex === index &&
                  selectedProject?.projectId === project.Id
                }
                onSelect={() => selectProject(index, project.Id)}
              />
            ))
          )}
        </div>
      )}
    </div>
  );
}
