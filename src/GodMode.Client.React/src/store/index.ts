/**
 * Global app state using Zustand.
 * Manages servers, connections, projects, and UI state.
 */
import { create } from 'zustand';
import { GodModeHub, type ConnectionState } from '../signalr/hub';
import type { ProjectSummary, ProjectRootInfo, ProfileInfo, ClaudeMessage } from '../signalr/types';
import { loadServers, saveServers, type ServerRegistration } from '../services/serverRegistry';
import { type QuestionState, emptyQuestion, detectQuestionFromMessage, detectQuestionFromStatus, looksLikeQuestion } from '../services/questionDetection';

const DISMISSED_KEY = 'godmode-dismissed-projects';
function loadDismissed(): Record<string, boolean> {
  try { return JSON.parse(localStorage.getItem(DISMISSED_KEY) || '{}'); } catch { return {}; }
}
function saveDismissed(dp: Record<string, boolean>) {
  localStorage.setItem(DISMISSED_KEY, JSON.stringify(dp));
}

export interface ServerState {
  registration: ServerRegistration;
  hub: GodModeHub;
  connectionState: ConnectionState;
  projects: ProjectSummary[];
  roots: ProjectRootInfo[];
  profiles: ProfileInfo[];
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
  restartServer: (index: number) => Promise<void>;
  refreshProjects: (index: number) => Promise<void>;

  // Selected project
  selectedProject: { serverIndex: number; projectId: string } | null;
  selectProject: (serverIndex: number, projectId: string) => void;
  clearSelection: () => void;

  // Project output (for the selected project)
  outputMessages: ClaudeMessage[];
  appendOutput: (projectId: string, message: ClaudeMessage) => void;
  clearOutput: () => void;

  // Question state (for the selected project's UI)
  question: QuestionState;
  lastInputSentAt: number;
  setQuestion: (q: QuestionState) => void;
  dismissQuestion: () => void;
  markInputSent: () => void;

  // Per-project client-side question detection (projectId → true if waiting)
  projectQuestions: Record<string, boolean>;

  // Per-project dismiss tracking (projectId → true if dismissed, cleared on Running)
  dismissedProjects: Record<string, boolean>;

  // Notification badges
  waitingCounts: Record<number, number>;
  totalWaitingCount: number;

  // Tile view
  isTileView: boolean;
  setTileView: (tile: boolean) => void;
  tileMessages: Record<string, ClaudeMessage[]>;
  tileLoading: Record<string, boolean>;
  appendTileMessage: (projectId: string, message: ClaudeMessage) => void;
  setTileMessages: (projectId: string, messages: ClaudeMessage[]) => void;
  setTileLoading: (projectId: string, loading: boolean) => void;
  clearTileMessages: () => void;

  // Profile filter
  profileFilter: string; // 'All' or a profile name
  setProfileFilter: (filter: string) => void;

  // Create project
  showCreateProject: boolean;
  setShowCreateProject: (show: boolean) => void;
  createProjectServerIndex: number | null;

  // UI
  showAddServer: boolean;
  setShowAddServer: (show: boolean) => void;
  editServerIndex: number | null;
  setEditServerIndex: (index: number | null) => void;
}

/** Recompute waiting counts from server projects + client-side question map, excluding dismissed */
function computeWaitingCounts(servers: ServerState[], projectQuestions: Record<string, boolean>, dismissedProjects: Record<string, boolean> = {}): { waitingCounts: Record<number, number>; totalWaitingCount: number } {
  const waitingCounts: Record<number, number> = {};
  let totalWaitingCount = 0;
  for (let i = 0; i < servers.length; i++) {
    const count = servers[i].projects.filter(p =>
      !dismissedProjects[p.Id] && (p.State === 'WaitingInput' || projectQuestions[p.Id])
    ).length;
    waitingCounts[i] = count;
    totalWaitingCount += count;
  }
  return { waitingCounts, totalWaitingCount };
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
      profiles: [],
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
      profiles: [],
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
          const counts = computeWaitingCounts(servers, state.projectQuestions, state.dismissedProjects);
          return { servers, ...counts };
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
          const sel = state.selectedProject;
          const clearSel = sel?.serverIndex === index && sel?.projectId === projectId;
          const pq = { ...state.projectQuestions };
          delete pq[projectId];
          const counts = computeWaitingCounts(servers, pq, state.dismissedProjects);
          return {
            servers,
            projectQuestions: pq,
            ...counts,
            ...(clearSel ? { selectedProject: null, outputMessages: [], question: emptyQuestion } : {}),
          };
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

          // Question management for the selected project
          const sel = state.selectedProject;
          let questionUpdate: Partial<AppState> = {};
          let pq = state.projectQuestions;
          if (sel?.serverIndex === index && sel?.projectId === status.Id && !state.dismissedProjects[status.Id]) {
            const isWaitingOrIdle = status.State === 'WaitingInput' || status.State === 'Idle';
            if (isWaitingOrIdle) {
              const project = servers[index]?.projects.find(p => p.Id === status.Id);
              if (project) {
                const detected = detectQuestionFromStatus(
                  status.State,
                  status.CurrentQuestion,
                  project.Name,
                  state.question,
                  state.lastInputSentAt,
                  state.outputMessages,
                );
                if (detected) {
                  questionUpdate = { question: detected };
                  pq = { ...pq, [status.Id]: detected.isActive };
                }
              }
            } else if (state.question.isActive) {
              questionUpdate = { question: emptyQuestion };
              pq = { ...pq, [status.Id]: false };
            }
          }

          // Clear projectQuestions and dismiss flag when project leaves waiting
          let dp = state.dismissedProjects;
          if (status.State === 'Running') {
            pq = { ...pq, [status.Id]: false };
            dp = { ...dp, [status.Id]: false };
            saveDismissed(dp);
          }

          const counts = computeWaitingCounts(servers, pq, dp);
          return { servers, projectQuestions: pq, dismissedProjects: dp, ...counts, ...questionUpdate };
        });
      },
      onOutputReceived: (projectId, message) => {
        const s = get();
        const sel = s.selectedProject;

        // Detect question from message for any project
        const isDismissed = s.dismissedProjects[projectId];
        const isQuestion = !isDismissed && (
          message.isQuestion ||
          (message.type === 'assistant' && message.contentSummary && looksLikeQuestion(message.contentSummary))
        );

        // Update per-project question tracking
        if (isQuestion) {
          set(state => {
            const pq = { ...state.projectQuestions, [projectId]: true };
            const counts = computeWaitingCounts(state.servers, pq, state.dismissedProjects);
            return { projectQuestions: pq, ...counts };
          });
        }

        // Feed selected project output + question state
        if (sel?.serverIndex === index && sel?.projectId === projectId) {
          const detected = detectQuestionFromMessage(message, s.question, s.lastInputSentAt, s.dismissedProjects[projectId]);
          set(state => ({
            outputMessages: [...state.outputMessages, message],
            ...(detected ? { question: detected } : {}),
          }));
        }

        // Feed tile messages when in tile view
        if (s.isTileView) {
          set(state => ({
            tileMessages: {
              ...state.tileMessages,
              [projectId]: [...(state.tileMessages[projectId] ?? []), message],
            },
          }));
        }
      },
      onCreationProgress: (_projectId, _message) => {
        // TODO: surface creation progress in UI
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
          servers[index] = { ...servers[index], projects: [], roots: [], profiles: [], connectionState: 'disconnected' };
        }
        const counts = computeWaitingCounts(servers, state.projectQuestions, state.dismissedProjects);
        return { servers, ...counts };
      });
    }
  },

  restartServer: async (index) => {
    await get().disconnectServer(index);
    await new Promise(resolve => setTimeout(resolve, 2000));
    await get().connectServer(index);
  },

  refreshProjects: async (index) => {
    const server = get().servers[index];
    if (!server || server.connectionState !== 'connected') return;

    try {
      const [projects, roots, profiles] = await Promise.all([
        server.hub.listProjects(),
        server.hub.listProjectRoots(),
        server.hub.listProfiles(),
      ]);
      set(state => {
        const servers = [...state.servers];
        if (servers[index]) {
          servers[index] = { ...servers[index], projects, roots, profiles };
        }
        const counts = computeWaitingCounts(servers, state.projectQuestions, state.dismissedProjects);
        return { servers, ...counts };
      });
    } catch (err) {
      console.error('Failed to refresh projects:', err);
    }
  },

  // Selection
  selectedProject: null,
  selectProject: (serverIndex, projectId) => {
    set({ selectedProject: { serverIndex, projectId }, outputMessages: [], question: emptyQuestion });
  },
  clearSelection: () => {
    set({ selectedProject: null, outputMessages: [], question: emptyQuestion });
  },

  // Output
  outputMessages: [],
  appendOutput: (_projectId, message) => {
    set(state => ({ outputMessages: [...state.outputMessages, message] }));
  },
  clearOutput: () => set({ outputMessages: [] }),

  // Question
  question: emptyQuestion,
  lastInputSentAt: 0,
  setQuestion: (q) => set({ question: q }),
  dismissQuestion: () => set(state => {
    const sel = state.selectedProject;
    const pq = sel ? { ...state.projectQuestions, [sel.projectId]: false } : state.projectQuestions;
    const dp = sel ? { ...state.dismissedProjects, [sel.projectId]: true } : state.dismissedProjects;
    saveDismissed(dp);
    const counts = computeWaitingCounts(state.servers, pq, dp);
    return { question: emptyQuestion, lastInputSentAt: Date.now(), projectQuestions: pq, dismissedProjects: dp, ...counts };
  }),
  markInputSent: () => set(state => {
    const sel = state.selectedProject;
    const pq = sel ? { ...state.projectQuestions, [sel.projectId]: false } : state.projectQuestions;
    const dp = sel ? { ...state.dismissedProjects, [sel.projectId]: false } : state.dismissedProjects;
    saveDismissed(dp);
    const counts = computeWaitingCounts(state.servers, pq, dp);
    return { question: emptyQuestion, lastInputSentAt: Date.now(), projectQuestions: pq, dismissedProjects: dp, ...counts };
  }),

  // Per-project questions
  projectQuestions: {},
  dismissedProjects: loadDismissed(),

  // Notifications
  waitingCounts: {},
  totalWaitingCount: 0,

  // Tile view
  isTileView: false,
  setTileView: (tile) => set({ isTileView: tile, tileMessages: {}, tileLoading: {}, selectedProject: null, outputMessages: [], question: emptyQuestion }),
  tileMessages: {},
  tileLoading: {},
  appendTileMessage: (projectId, message) => {
    set(state => ({
      tileMessages: {
        ...state.tileMessages,
        [projectId]: [...(state.tileMessages[projectId] ?? []), message],
      },
    }));
  },
  setTileMessages: (projectId, messages) => {
    set(state => ({
      tileMessages: { ...state.tileMessages, [projectId]: messages },
    }));
  },
  setTileLoading: (projectId, loading) => {
    set(state => ({
      tileLoading: { ...state.tileLoading, [projectId]: loading },
    }));
  },
  clearTileMessages: () => set({ tileMessages: {}, tileLoading: {} }),

  // Profile filter
  profileFilter: 'All',
  setProfileFilter: (filter) => set({ profileFilter: filter }),

  // Create project
  showCreateProject: false,
  setShowCreateProject: (show) => set({ showCreateProject: show, createProjectServerIndex: show ? null : null }),
  createProjectServerIndex: null,

  // UI
  showAddServer: false,
  setShowAddServer: (show) => set({ showAddServer: show }),
  editServerIndex: null,
  setEditServerIndex: (index) => set({ editServerIndex: index }),
}));
