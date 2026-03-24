import { useMemo } from 'react';
import { useAppStore, type ServerState } from '../../store';
import { ServerItem } from './ServerItem';
import { ProjectItem } from './ProjectItem';
import './Sidebar.css';

export function Sidebar() {
  const servers = useAppStore(s => s.servers);
  const profileFilter = useAppStore(s => s.profileFilter);

  return (
    <div className="sidebar">
      <div className="sidebar-header">
        <img src="/godmodelogo.png" alt="GodMode" className="sidebar-logo" />
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
            <ServerSection key={index} server={server} index={index} profileFilter={profileFilter} />
          ))
        )}
      </div>
    </div>
  );
}

function ServerSection({ server, index, profileFilter }: { server: ServerState; index: number; profileFilter: string }) {
  const connectServer = useAppStore(s => s.connectServer);
  const disconnectServer = useAppStore(s => s.disconnectServer);
  const restartServer = useAppStore(s => s.restartServer);
  const removeServer = useAppStore(s => s.removeServer);
  const selectProject = useAppStore(s => s.selectProject);
  const selectedProject = useAppStore(s => s.selectedProject);
  const setEditServerIndex = useAppStore(s => s.setEditServerIndex);

  // Group projects by profile, applying filter
  const profileGroups = useMemo(() => {
    const groups = new Map<string, { description?: string | null; projects: typeof server.projects }>();

    // Seed profile names from server profiles
    for (const p of server.profiles) {
      groups.set(p.Name, { description: p.Description, projects: [] });
    }

    // Assign projects to their profile group
    for (const project of server.projects) {
      const name = project.ProfileName ?? 'Default';
      if (profileFilter !== 'All' && name !== profileFilter) continue;
      if (!groups.has(name)) {
        groups.set(name, { description: null, projects: [] });
      }
      groups.get(name)!.projects.push(project);
    }

    // If filtering, remove empty groups
    if (profileFilter !== 'All') {
      for (const [name, g] of groups) {
        if (g.projects.length === 0) groups.delete(name);
      }
    }

    return [...groups.entries()].map(([name, g]) => ({
      profileName: name,
      description: g.description,
      projects: g.projects,
    }));
  }, [server.projects, server.profiles, profileFilter]);

  const hasMultipleProfiles = profileGroups.length > 1 || (profileGroups.length === 1 && profileGroups[0].profileName !== 'Default');
  const filteredProjects = profileFilter === 'All'
    ? server.projects
    : server.projects.filter(p => (p.ProfileName ?? 'Default') === profileFilter);

  return (
    <div className="server-section">
      <ServerItem
        server={server}
        onConnect={() => connectServer(index)}
        onDisconnect={() => disconnectServer(index)}
        onRestart={() => restartServer(index)}
        onEdit={() => setEditServerIndex(index)}
        onRemove={() => removeServer(index)}
      />
      {server.connectionState === 'connected' && (
        <div className="project-list">
          {filteredProjects.length === 0 ? (
            <div className="project-list-empty">No projects</div>
          ) : hasMultipleProfiles ? (
            profileGroups.map(group => (
              <ProfileSection key={group.profileName} group={group}>
                {group.projects.map(project => (
                  <ProjectItem
                    key={project.Id}
                    project={project}
                    isSelected={
                      selectedProject?.serverIndex === index &&
                      selectedProject?.projectId === project.Id
                    }
                    onSelect={() => selectProject(index, project.Id)}
                  />
                ))}
              </ProfileSection>
            ))
          ) : (
            filteredProjects.map(project => (
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

function ProfileSection({ group, children }: { group: { profileName: string; description?: string | null; projects: unknown[] }; children: React.ReactNode }) {
  if (group.projects.length === 0) return null;

  return (
    <div className="profile-group">
      <div className="profile-group-header">
        <span className="profile-group-name">{group.profileName}</span>
        <span className="profile-group-count">{group.projects.length}</span>
      </div>
      {children}
    </div>
  );
}
