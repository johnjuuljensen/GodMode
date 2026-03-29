import { useMemo } from 'react';
import { useAppStore, type ServerState } from '../../store';
import { ServerItem } from './ServerItem';
import { ProjectItem } from './ProjectItem';
import './Sidebar.css';

export function Sidebar() {
  const servers = useAppStore(s => s.servers);
  const profileFilter = useAppStore(s => s.profileFilter);
  const setShowAddServer = useAppStore(s => s.setShowAddServer);

  return (
    <div className="sidebar">
      <div className="sidebar-header">
        <span className="sidebar-title">GodMode</span>
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
          servers.map(server => (
            <ServerSection key={server.registration.url} server={server} profileFilter={profileFilter} />
          ))
        )}
      </div>
    </div>
  );
}

function ServerSection({ server, profileFilter }: { server: ServerState; profileFilter: string }) {
  const connectServer = useAppStore(s => s.connectServer);
  const disconnectServer = useAppStore(s => s.disconnectServer);
  const restartServer = useAppStore(s => s.restartServer);
  const removeServer = useAppStore(s => s.removeServer);
  const selectProject = useAppStore(s => s.selectProject);
  const selectedProject = useAppStore(s => s.selectedProject);
  const setEditServerId = useAppStore(s => s.setEditServerId);

  const serverId = server.registration.url;

  // Group projects by profile, applying filter
  const profileGroups = useMemo(() => {
    const groups = new Map<string, { description?: string | null; projects: typeof server.projects }>();

    for (const p of server.profiles) {
      groups.set(p.Name, { description: p.Description, projects: [] });
    }

    for (const project of server.projects) {
      const name = project.ProfileName ?? '';
      if (profileFilter !== 'All' && name !== profileFilter) continue;
      if (!groups.has(name)) {
        groups.set(name, { description: null, projects: [] });
      }
      groups.get(name)!.projects.push(project);
    }

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

  const hasMultipleProfiles = profileGroups.length > 1 || (profileGroups.length === 1 && profileGroups[0].profileName !== '');
  const filteredProjects = profileFilter === 'All'
    ? server.projects
    : server.projects.filter(p => (p.ProfileName ?? '') === profileFilter);

  return (
    <div className="server-section">
      <ServerItem
        server={server}
        onConnect={() => connectServer(serverId)}
        onDisconnect={() => disconnectServer(serverId)}
        onRestart={() => restartServer(serverId)}
        onEdit={() => setEditServerId(serverId)}
        onRemove={() => removeServer(serverId)}
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
                      selectedProject?.serverId === serverId &&
                      selectedProject?.projectId === project.Id
                    }
                    onSelect={() => selectProject(serverId, project.Id)}
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
                  selectedProject?.serverId === serverId &&
                  selectedProject?.projectId === project.Id
                }
                onSelect={() => selectProject(serverId, project.Id)}
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
