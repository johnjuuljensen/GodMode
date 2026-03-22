/**
 * Global app state using Zustand.
 * Manages servers, connections, projects, and UI state.
 */
import { create } from 'zustand';
import { GodModeHub, type ConnectionState } from '../signalr/hub';
import type { ProjectSummary, ProjectRootInfo, ClaudeMessage } from '../signalr/types';
import { loadServers, saveServers, type ServerRegistration } from '../services/serverRegistry';

export interface ServerState {
  registration: ServerRegistration;
  hub: GodModeHub;
  connectionState: ConnectionState;
  projects: ProjectSummary[];
  roots: ProjectRootInfo[];
}

interface AppState {
  // Servers
  servers: ServerState[];
  loadServers: () => void;
  addServer: (reg: ServerRegistration) => void;
  updateServer: (index: number, reg: ServerRegistration) => void;
  removeServer: (index: number) => void;
  connectServer: (index: number) => Promise<void>;
  disconnectServer: (index: number) => Promise<void>;
  refreshProjects: (index: number) => Promise<void>;

  // Selected project
  selectedProject: { serverIndex: number; projectId: string } | null;
  selectProject: (serverIndex: number, projectId: string) => void;
  clearSelection: () => void;

  // Project output (for the selected project)
  outputMessages: ClaudeMessage[];
  appendOutput: (projectId: string, message: ClaudeMessage) => void;
  clearOutput: () => void;

  // UI
  showAddServer: boolean;
  setShowAddServer: (show: boolean) => void;
  editServerIndex: number | null;
  setEditServerIndex: (index: number | null) => void;
}

export const useAppStore = create<AppState>((set, get) => ({
  servers: [],

  loadServers: () => {
    const registrations = loadServers();
    const servers = registrations.map(reg => ({
      registration: reg,
      hub: new GodModeHub(),
      connectionState: 'disconnected' as ConnectionState,
      projects: [],
      roots: [],
    }));
    set({ servers });
  },

  addServer: (reg) => {
    const hub = new GodModeHub();
    const serverState: ServerState = {
      registration: reg,
      hub,
      connectionState: 'disconnected',
      projects: [],
      roots: [],
    };
    set(state => {
      const servers = [...state.servers, serverState];
      saveServers(servers.map(s => s.registration));
      return { servers, showAddServer: false };
    });
  },

  updateServer: (index, reg) => {
    set(state => {
      const servers = [...state.servers];
      if (index >= 0 && index < servers.length) {
        servers[index] = { ...servers[index], registration: reg };
        saveServers(servers.map(s => s.registration));
      }
      return { servers, editServerIndex: null };
    });
  },

  removeServer: (index) => {
    const server = get().servers[index];
    if (server) {
      server.hub.disconnect();
    }
    set(state => {
      const servers = state.servers.filter((_, i) => i !== index);
      saveServers(servers.map(s => s.registration));
      return { servers, editServerIndex: null };
    });
  },

  connectServer: async (index) => {
    const state = get();
    const server = state.servers[index];
    if (!server) return;

    const updateConnectionState = (connectionState: ConnectionState) => {
      set(state => {
        const servers = [...state.servers];
        if (servers[index]) {
          servers[index] = { ...servers[index], connectionState };
        }
        return { servers };
      });
    };

    server.hub.setCallbacks({
      onStateChanged: updateConnectionState,
      onProjectCreated: (status) => {
        set(state => {
          const servers = [...state.servers];
          if (servers[index]) {
            const summary: ProjectSummary = {
              Id: status.Id,
              Name: status.Name,
              State: status.State,
              UpdatedAt: status.UpdatedAt,
              CurrentQuestion: status.CurrentQuestion,
              RootName: status.RootName,
              ProfileName: status.ProfileName,
            };
            servers[index] = {
              ...servers[index],
              projects: [...servers[index].projects, summary],
            };
          }
          return { servers };
        });
      },
      onProjectDeleted: (projectId) => {
        set(state => {
          const servers = [...state.servers];
          if (servers[index]) {
            servers[index] = {
              ...servers[index],
              projects: servers[index].projects.filter(p => p.Id !== projectId),
            };
          }
          // Clear selection if deleted project was selected
          const sel = state.selectedProject;
          const clearSel = sel?.serverIndex === index && sel?.projectId === projectId;
          return { servers, ...(clearSel ? { selectedProject: null, outputMessages: [] } : {}) };
        });
      },
      onStatusChanged: (_projectId, status) => {
        set(state => {
          const servers = [...state.servers];
          if (servers[index]) {
            servers[index] = {
              ...servers[index],
              projects: servers[index].projects.map(p =>
                p.Id === status.Id
                  ? { ...p, State: status.State, UpdatedAt: status.UpdatedAt, CurrentQuestion: status.CurrentQuestion }
                  : p
              ),
            };
          }
          return { servers };
        });
      },
      onOutputReceived: (projectId, message) => {
        const sel = get().selectedProject;
        if (sel?.serverIndex === index && sel?.projectId === projectId) {
          set(state => ({ outputMessages: [...state.outputMessages, message] }));
        }
      },
      onCreationProgress: (_projectId, _message) => {
        // TODO: Phase 3 - surface creation progress in UI
      },
    });

    try {
      await server.hub.connect(server.registration.url, server.registration.accessToken);
      await get().refreshProjects(index);
    } catch (err) {
      console.error('Failed to connect to server:', err);
      updateConnectionState('disconnected');
    }
  },

  disconnectServer: async (index) => {
    const server = get().servers[index];
    if (server) {
      await server.hub.disconnect();
      set(state => {
        const servers = [...state.servers];
        if (servers[index]) {
          servers[index] = { ...servers[index], projects: [], roots: [], connectionState: 'disconnected' };
        }
        return { servers };
      });
    }
  },

  refreshProjects: async (index) => {
    const server = get().servers[index];
    if (!server || server.connectionState !== 'connected') return;

    try {
      const [projects, roots] = await Promise.all([
        server.hub.listProjects(),
        server.hub.listProjectRoots(),
      ]);
      set(state => {
        const servers = [...state.servers];
        if (servers[index]) {
          servers[index] = { ...servers[index], projects, roots };
        }
        return { servers };
      });
    } catch (err) {
      console.error('Failed to refresh projects:', err);
    }
  },

  // Selection
  selectedProject: null,
  selectProject: (serverIndex, projectId) => {
    set({ selectedProject: { serverIndex, projectId }, outputMessages: [] });
  },
  clearSelection: () => {
    set({ selectedProject: null, outputMessages: [] });
  },

  // Output
  outputMessages: [],
  appendOutput: (_projectId, message) => {
    set(state => ({ outputMessages: [...state.outputMessages, message] }));
  },
  clearOutput: () => set({ outputMessages: [] }),

  // UI
  showAddServer: false,
  setShowAddServer: (show) => set({ showAddServer: show }),
  editServerIndex: null,
  setEditServerIndex: (index) => set({ editServerIndex: index }),
}));
