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

/** The serverId is the server's registration URL */
function getServerId(server: ServerState): string {
  return server.registration.url;
}

function findServer(servers: ServerState[], serverId: string): ServerState | undefined {
  return servers.find(s => s.registration.url === serverId);
}

function mapServer(servers: ServerState[], serverId: string, fn: (s: ServerState) => ServerState): ServerState[] {
  return servers.map(s => s.registration.url === serverId ? fn(s) : s);
}

interface AppState {
  // Servers
  servers: ServerState[];
  loadServers: () => void;
  addServer: (reg: ServerRegistration) => void;
  updateServer: (serverId: string, reg: ServerRegistration) => void;
  removeServer: (serverId: string) => void;
  connectServer: (serverId: string) => Promise<void>;
  disconnectServer: (serverId: string) => Promise<void>;
  restartServer: (serverId: string) => Promise<void>;
  refreshProjects: (serverId: string) => Promise<void>;

  // Selected project
  selectedProject: { serverId: string; projectId: string } | null;
  selectProject: (serverId: string, projectId: string) => void;
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
  waitingCounts: Record<string, number>;
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
  createProjectServerId: string | null;

  // UI
  showAddServer: boolean;
  setShowAddServer: (show: boolean) => void;
  editServerId: string | null;
  setEditServerId: (id: string | null) => void;

  // MCP Browser
  showMcpBrowser: boolean;
  mcpBrowserContext: { serverId: string; profileName: string; rootName?: string; actionName?: string } | null;
  setShowMcpBrowser: (show: boolean, context?: { serverId: string; profileName: string; rootName?: string; actionName?: string }) => void;

  // MCP Profile Panel
  showMcpProfile: boolean;
  mcpProfileContext: { serverId: string; profileName: string } | null;
  setShowMcpProfile: (show: boolean, context?: { serverId: string; profileName: string }) => void;

  // Profile Settings
  showProfileSettings: boolean;
  profileSettingsContext: { serverId: string; profileName: string } | null;
  setShowProfileSettings: (show: boolean, context?: { serverId: string; profileName: string }) => void;

  // Create Profile
  showCreateProfile: boolean;
  createProfileServerId: string | null;
  setShowCreateProfile: (show: boolean, serverId?: string) => void;

  // Root Manager
  showRootManager: boolean;
  rootManagerServerId: string | null;
  rootManagerInitialTab: 'create' | 'import' | null;
  setShowRootManager: (show: boolean, serverId?: string, initialTab?: 'create' | 'import') => void;

  // App Settings
  showAppSettings: boolean;
  setShowAppSettings: (show: boolean) => void;
  featureVisibility: {
    roots: boolean;
    mcp: boolean;
    profiles: boolean;
  };
  setFeatureVisibility: (key: 'roots' | 'mcp' | 'profiles', visible: boolean) => void;
}

/** Recompute waiting counts from server projects + client-side question map, excluding dismissed */
function computeWaitingCounts(servers: ServerState[], projectQuestions: Record<string, boolean>, dismissedProjects: Record<string, boolean> = {}): { waitingCounts: Record<string, number>; totalWaitingCount: number } {
  const waitingCounts: Record<string, number> = {};
  let totalWaitingCount = 0;
  for (const server of servers) {
    const id = getServerId(server);
    const count = server.projects.filter(p =>
      !dismissedProjects[p.Id] && (p.State === 'WaitingInput' || projectQuestions[p.Id])
    ).length;
    waitingCounts[id] = count;
    totalWaitingCount += count;
  }
  return { waitingCounts, totalWaitingCount };
}

export const useAppStore = create<AppState>((set, get) => ({
  servers: [],

  loadServers: () => {
    const registrations = loadServers();

    // Auto-detect hosted mode: if served from an http(s) origin, check for the server API
    const origin = window.location.origin;
    const isHttp = origin.startsWith('http');
    if (isHttp && !registrations.some(r => r.url.replace(/\/$/, '') === origin)) {
      // Probe the server info endpoint to confirm we're hosted
      fetch(`${origin}/api/info`).then(res => {
        if (res.ok) return res.json();
        throw new Error('Not hosted');
      }).then((info: Record<string, unknown>) => {
        if (info.hosted) {
          const reg: ServerRegistration = { url: origin, displayName: 'This Server' };
          get().addServer(reg);
          get().connectServer(origin);
        }
      }).catch(() => { /* Not hosted — use localStorage servers */ });
    }

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

  updateServer: (serverId, reg) => {
    set(state => {
      const servers = mapServer(state.servers, serverId, s => ({ ...s, registration: reg }));
      saveServers(servers.map(s => s.registration));
      return { servers, editServerId: null };
    });
  },

  removeServer: (serverId) => {
    const server = findServer(get().servers, serverId);
    if (server) {
      server.hub.disconnect();
    }
    set(state => {
      const servers = state.servers.filter(s => s.registration.url !== serverId);
      saveServers(servers.map(s => s.registration));
      return { servers, editServerId: null };
    });
  },

  connectServer: async (serverId) => {
    const state = get();
    const server = findServer(state.servers, serverId);
    if (!server) return;

    const updateConnectionState = (connectionState: ConnectionState) => {
      set(state => {
        const servers = mapServer(state.servers, serverId, s => ({ ...s, connectionState }));
        return { servers };
      });
    };

    server.hub.setCallbacks({
      onStateChanged: updateConnectionState,
      onProjectCreated: (status) => {
        set(state => {
          const summary: ProjectSummary = {
            Id: status.Id,
            Name: status.Name,
            State: status.State,
            UpdatedAt: status.UpdatedAt,
            CurrentQuestion: status.CurrentQuestion,
            RootName: status.RootName,
            ProfileName: status.ProfileName,
          };
          const servers = mapServer(state.servers, serverId, s => ({
            ...s,
            projects: [...s.projects, summary],
          }));
          const counts = computeWaitingCounts(servers, state.projectQuestions, state.dismissedProjects);
          return { servers, ...counts };
        });
      },
      onProjectDeleted: (projectId) => {
        set(state => {
          const servers = mapServer(state.servers, serverId, s => ({
            ...s,
            projects: s.projects.filter(p => p.Id !== projectId),
          }));
          const sel = state.selectedProject;
          const clearSel = sel?.serverId === serverId && sel?.projectId === projectId;
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
          const servers = mapServer(state.servers, serverId, s => ({
            ...s,
            projects: s.projects.map(p =>
              p.Id === status.Id
                ? { ...p, State: status.State, UpdatedAt: status.UpdatedAt, CurrentQuestion: status.CurrentQuestion }
                : p
            ),
          }));

          // Question management for the selected project
          const sel = state.selectedProject;
          let questionUpdate: Partial<AppState> = {};
          let pq = state.projectQuestions;
          if (sel?.serverId === serverId && sel?.projectId === status.Id && !state.dismissedProjects[status.Id]) {
            const isWaitingOrIdle = status.State === 'WaitingInput' || status.State === 'Idle';
            if (isWaitingOrIdle) {
              const currentServer = findServer(servers, serverId);
              const project = currentServer?.projects.find(p => p.Id === status.Id);
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
        if (sel?.serverId === serverId && sel?.projectId === projectId) {
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
      await get().refreshProjects(serverId);
    } catch (err) {
      console.error('Failed to connect to server:', err);
      updateConnectionState('disconnected');
    }
  },

  disconnectServer: async (serverId) => {
    const server = findServer(get().servers, serverId);
    if (server) {
      await server.hub.disconnect();
      set(state => {
        const servers = mapServer(state.servers, serverId, s => ({
          ...s, projects: [], roots: [], profiles: [], connectionState: 'disconnected',
        }));
        const counts = computeWaitingCounts(servers, state.projectQuestions, state.dismissedProjects);
        return { servers, ...counts };
      });
    }
  },

  restartServer: async (serverId) => {
    await get().disconnectServer(serverId);
    await new Promise(resolve => setTimeout(resolve, 2000));
    await get().connectServer(serverId);
  },

  refreshProjects: async (serverId) => {
    const server = findServer(get().servers, serverId);
    if (!server || server.connectionState !== 'connected') return;

    try {
      const [projects, roots, profiles] = await Promise.all([
        server.hub.listProjects(),
        server.hub.listProjectRoots(),
        server.hub.listProfiles(),
      ]);
      set(state => {
        const servers = mapServer(state.servers, serverId, s => ({ ...s, projects, roots, profiles }));
        const counts = computeWaitingCounts(servers, state.projectQuestions, state.dismissedProjects);
        return { servers, ...counts };
      });
    } catch (err) {
      console.error('Failed to refresh projects:', err);
    }
  },

  // Selection
  selectedProject: null,
  selectProject: (serverId, projectId) => {
    set({ selectedProject: { serverId, projectId }, outputMessages: [], question: emptyQuestion });
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
  setShowCreateProject: (show) => set({ showCreateProject: show, createProjectServerId: show ? null : null }),
  createProjectServerId: null,

  // UI
  showAddServer: false,
  setShowAddServer: (show) => set({ showAddServer: show }),
  editServerId: null,
  setEditServerId: (id) => set({ editServerId: id }),

  // MCP Browser
  showMcpBrowser: false,
  mcpBrowserContext: null,
  setShowMcpBrowser: (show, context) => set({ showMcpBrowser: show, mcpBrowserContext: context ?? null }),

  // MCP Profile Panel
  showMcpProfile: false,
  mcpProfileContext: null,
  setShowMcpProfile: (show, context) => set({ showMcpProfile: show, mcpProfileContext: context ?? null }),

  // Profile Settings
  showProfileSettings: false,
  profileSettingsContext: null,
  setShowProfileSettings: (show, context) => set({ showProfileSettings: show, profileSettingsContext: context ?? null }),

  // Create Profile
  showCreateProfile: false,
  createProfileServerId: null,
  setShowCreateProfile: (show, serverId) => set({ showCreateProfile: show, createProfileServerId: serverId ?? null }),

  // Root Manager
  showRootManager: false,
  rootManagerServerId: null,
  rootManagerInitialTab: null,
  setShowRootManager: (show, serverId, initialTab) => set({ showRootManager: show, rootManagerServerId: serverId ?? null, rootManagerInitialTab: initialTab ?? null }),

  // App Settings
  showAppSettings: false,
  setShowAppSettings: (show) => set({ showAppSettings: show }),
  featureVisibility: (() => {
    try {
      const stored = localStorage.getItem('godmode-feature-visibility');
      if (stored) return JSON.parse(stored);
    } catch { /* ignore */ }
    return { roots: true, mcp: true, profiles: true };
  })(),
  setFeatureVisibility: (key, visible) => set(state => {
    const next = { ...state.featureVisibility, [key]: visible };
    localStorage.setItem('godmode-feature-visibility', JSON.stringify(next));
    return { featureVisibility: next };
  }),
}));
