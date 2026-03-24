import { useMemo } from 'react';
import { useAppStore, type ServerState } from '../../store';
import { ServerItem } from './ServerItem';
import { ProjectItem } from './ProjectItem';
import './Sidebar.css';

export function Sidebar() {
  const servers = useAppStore(s => s.servers);
  const setShowAddServer = useAppStore(s => s.setShowAddServer);
  const setShowCreateProject = useAppStore(s => s.setShowCreateProject);
  const profileFilter = useAppStore(s => s.profileFilter);

  const hasRoots = servers.some(s => s.roots.length > 0);

  return (
    <div className="sidebar">
      <div className="sidebar-header">
        <span className="sidebar-title">GodMode</span>
        <div className="sidebar-header-actions">
          {hasRoots && (
            <button
              className="sidebar-add-btn"
              onClick={() => setShowCreateProject(true)}
              title="Create project"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
                <line x1="12" y1="8" x2="12" y2="14" />
                <line x1="9" y1="11" x2="15" y2="11" />
              </svg>
            </button>
          )}
          <button
            className="sidebar-add-btn"
            onClick={() => setShowAddServer(true)}
            title="Add server"
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="2" y="2" width="20" height="8" rx="2" ry="2" />
              <rect x="2" y="14" width="20" height="8" rx="2" ry="2" />
              <line x1="6" y1="6" x2="6.01" y2="6" />
              <line x1="6" y1="18" x2="6.01" y2="18" />
            </svg>
          </button>
        </div>
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
