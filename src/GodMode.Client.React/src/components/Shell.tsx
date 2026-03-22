import { useAppStore } from '../store';
import { Sidebar } from './Sidebar/Sidebar';
import { ProjectView } from './Project/ProjectView';
import { AddServer } from './Servers/AddServer';
import { EditServer } from './Servers/EditServer';
import './Shell.css';

export function Shell() {
  const selectedProject = useAppStore(s => s.selectedProject);
  const showAddServer = useAppStore(s => s.showAddServer);
  const editServerIndex = useAppStore(s => s.editServerIndex);

  return (
    <div className="shell">
      <div className="shell-sidebar">
        <Sidebar />
      </div>
      <div className="shell-content">
        {selectedProject ? (
          <ProjectView
            serverIndex={selectedProject.serverIndex}
            projectId={selectedProject.projectId}
          />
        ) : (
          <div className="shell-empty">
            <p>Select a project from the sidebar</p>
          </div>
        )}
      </div>

      {showAddServer && <AddServer />}
      {editServerIndex !== null && <EditServer index={editServerIndex} />}
    </div>
  );
}
