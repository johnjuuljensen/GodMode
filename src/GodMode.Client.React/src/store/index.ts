/**
 * Global app state using Zustand.
 * Matches Avalonia's data model: Profile → Root:Server → Projects.
 */
import { create } from 'zustand';
import { GodModeHub, type ConnectionState } from '../signalr/hub';
import type {
  ProjectSummary, ProjectRootInfo, ProfileInfo, ClaudeMessage,
  ServerInfo, CreateActionInfo,
} from '../signalr/types';
import * as api from '../services/hostApi';
import type { AddServerRequest } from '../services/hostApi';
import {
  type QuestionState, emptyQuestion, detectQuestionFromMessage,
  detectQuestionFromStatus, looksLikeQuestion,
} from '../services/questionDetection';

// ── Persisted dismiss tracking ─────────────────────────────────
const DISMISSED_KEY = 'godmode-dismissed-projects';
function loadDismissed(): Record<string, boolean> {
  try { return JSON.parse(localStorage.getItem(DISMISSED_KEY) || '{}'); } catch { return {}; }
}
function saveDismissed(dp: Record<string, boolean>) {
  localStorage.setItem(DISMISSED_KEY, JSON.stringify(dp));
}

// ── Computed view model types ──────────────────────────────────

export interface ServerConnection {
  serverInfo: ServerInfo;
  hub: GodModeHub;
  connectionState: ConnectionState;
  projects: ProjectSummary[];
  roots: ProjectRootInfo[];
  profiles: ProfileInfo[];
}

export interface RootGroup {
  name: string;          // display name (qualified with server name if multi-server)
  rootName: string;      // actual root name for API calls
  profileName: string;
  serverId: string;
  serverName: string;
  projects: ProjectSummary[];
  actions: CreateActionInfo[];
}

export interface ProfileGroup {
  name: string;
  rootGroups: RootGroup[];
  projectCount: number;
}

// ── Store interface ────────────────────────────────────────────

interface AppState {
  // Raw server connections
  serverConnections: ServerConnection[];
  getConnection: (serverId: string) => ServerConnection | undefined;
  getHub: (serverId: string) => GodModeHub | undefined;

  // Computed hierarchy (rebuilt from serverConnections)
  profileGroups: ProfileGroup[];
  inactiveServers: ServerConnection[];
  profileFilterOptions: string[];
  profileFilter: string;
  setProfileFilter: (filter: string) => void;

  // Server lifecycle
  loadServers: () => Promise<void>;
  addServer: (req: AddServerRequest) => Promise<void>;
  removeServer: (serverId: string) => Promise<void>;
  connectServer: (serverId: string) => Promise<void>;
  disconnectServer: (serverId: string) => Promise<void>;
  startServer: (serverId: string) => Promise<void>;
  refreshProjects: (serverId: string) => Promise<void>;

  // Selected project (by serverId + projectId)
  selectedProject: { serverId: string; projectId: string } | null;
  selectProject: (serverId: string, projectId: string) => void;
  clearSelection: () => void;

  // Project output
  outputMessages: ClaudeMessage[];
  appendOutput: (projectId: string, message: ClaudeMessage) => void;
  clearOutput: () => void;

  // Question state
  question: QuestionState;
  lastInputSentAt: number;
  setQuestion: (q: QuestionState) => void;
  dismissQuestion: () => void;
  markInputSent: () => void;

  // Per-project question tracking
  projectQuestions: Record<string, boolean>;
  dismissedProjects: Record<string, boolean>;

  // Notification badges
  totalWaitingCount: number;

  // Tile view
  isTileView: boolean;
  setTileView: (tile: boolean) => void;
  tileMessages: Record<string, ClaudeMessage[]>;
  tileLoading: Record<string, boolean>;
  setTileLoading: (projectId: string, loading: boolean) => void;
  clearTileMessages: () => void;

  // UI dialogs
  showAddServer: boolean;
  setShowAddServer: (show: boolean) => void;
  showCreateProject: boolean;
  createProjectContext: { serverId: string; rootName: string } | null;
  setShowCreateProject: (show: boolean, context?: { serverId: string; rootName: string }) => void;
  editServerId: string | null;
  setEditServerId: (id: string | null) => void;
  showRootManager: boolean;
  setShowRootManager: (show: boolean) => void;
  showProfileSettings: boolean;
  setShowProfileSettings: (show: boolean) => void;
}

// ── Helper: rebuild profile hierarchy ──────────────────────────

function rebuildHierarchy(
  connections: ServerConnection[],
  filter: string,
): { profileGroups: ProfileGroup[]; inactiveServers: ServerConnection[]; profileFilterOptions: string[] } {
  const profileDict = new Map<string, RootGroup[]>();
  const allProfileNames = new Set<string>();
  const multiServer = connections.filter(c => c.connectionState === 'connected').length > 1;
  const representedServerIds = new Set<string>();

  for (const conn of connections) {
    if (conn.connectionState !== 'connected') continue;

    // Group projects by root, then by profile
    const projectsByRoot = new Map<string, ProjectSummary[]>();
    for (const p of conn.projects) {
      const rootName = p.RootName ?? 'default';
      if (!projectsByRoot.has(rootName)) projectsByRoot.set(rootName, []);
      projectsByRoot.get(rootName)!.push(p);
    }

    // Process each known root (includes empty roots)
    for (const root of conn.roots) {
      const profileName = root.ProfileName ?? 'Default';
      allProfileNames.add(profileName);

      const projects = projectsByRoot.get(root.Name) ?? [];
      // Projects might have a different ProfileName than the root default
      const projectsByProfile = new Map<string, ProjectSummary[]>();
      projectsByProfile.set(profileName, []); // ensure root's profile exists even if empty
      for (const p of projects) {
        const pProfile = p.ProfileName ?? 'Default';
        allProfileNames.add(pProfile);
        if (!projectsByProfile.has(pProfile)) projectsByProfile.set(pProfile, []);
        projectsByProfile.get(pProfile)!.push(p);
      }

      for (const [pName, pProjects] of projectsByProfile) {
        if (!profileDict.has(pName)) profileDict.set(pName, []);
        const rootList = profileDict.get(pName)!;

        const existing = rootList.find(r => r.rootName === root.Name && r.serverId === conn.serverInfo.Id);
        if (existing) {
          for (const p of pProjects) {
            if (!existing.projects.some(ep => ep.Id === p.Id))
              existing.projects.push(p);
          }
        } else {
          rootList.push({
            name: multiServer ? `${root.Name} (${conn.serverInfo.Name})` : root.Name,
            rootName: root.Name,
            profileName: pName,
            serverId: conn.serverInfo.Id,
            serverName: conn.serverInfo.Name,
            projects: [...pProjects],
            actions: root.Actions ?? [],
          });
        }
        representedServerIds.add(conn.serverInfo.Id);
      }
    }
  }

  // Apply filter
  const filtered = filter === 'All'
    ? [...profileDict.entries()]
    : [...profileDict.entries()].filter(([name]) => name.toLowerCase() === filter.toLowerCase());

  const profileGroups = filtered
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([name, rootGroups]) => ({
      name,
      rootGroups,
      projectCount: rootGroups.reduce((sum, r) => sum + r.projects.length, 0),
    }));

  const inactiveServers = connections.filter(c => !representedServerIds.has(c.serverInfo.Id));

  const profileFilterOptions = ['All', ...Array.from(allProfileNames).sort()];

  return { profileGroups, inactiveServers, profileFilterOptions };
}

function computeTotalWaiting(connections: ServerConnection[], pq: Record<string, boolean>, dp: Record<string, boolean>): number {
  let total = 0;
  for (const conn of connections) {
    for (const p of conn.projects) {
      if (!dp[p.Id] && (p.State === 'WaitingInput' || pq[p.Id])) total++;
    }
  }
  return total;
}

// ── Store ──────────────────────────────────────────────────────

export const useAppStore = create<AppState>((set, get) => ({
  serverConnections: [],
  profileGroups: [],
  inactiveServers: [],
  profileFilterOptions: ['All'],
  profileFilter: 'All',

  getConnection: (serverId) => get().serverConnections.find(c => c.serverInfo.Id === serverId),
  getHub: (serverId) => get().serverConnections.find(c => c.serverInfo.Id === serverId)?.hub,

  setProfileFilter: (filter) => {
    const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(get().serverConnections, filter);
    set({ profileFilter: filter, profileGroups, inactiveServers, profileFilterOptions });
  },

  // ── Server lifecycle ──────────────────────────────────────

  loadServers: async () => {
    console.info('[store] loadServers: waiting for host API');
    await api.waitUntilReady();
    try {
      const servers = await api.fetchServers();
      console.info(`[store] loadServers: fetched ${servers.length} servers:`, servers.map(s => `${s.Name}(${s.State})`));
      const existing = get().serverConnections;

      // Preserve hubs and connection state for servers that are still present
      const connections: ServerConnection[] = servers.map(s => {
        const prev = existing.find(c => c.serverInfo.Id === s.Id);
        if (prev) {
          return { ...prev, serverInfo: s };
        }
        return {
          serverInfo: s,
          hub: new GodModeHub(),
          connectionState: 'disconnected' as ConnectionState,
          projects: [],
          roots: [],
          profiles: [],
        };
      });

      const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, get().profileFilter);
      console.info(`[store] loadServers: ${profileGroups.length} profiles, ${inactiveServers.length} inactive`);
      set({ serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions });

      // Subscribe to SSE events (once)
      if (existing.length === 0) {
        console.info('[store] loadServers: subscribing to events');
        api.subscribeEvents((type) => {
          if (type === 'serversChanged') {
            console.info('[store] SSE: serversChanged');
            get().loadServers();
          }
        });
      }

      // Auto-connect to servers that aren't already connected
      // Skip codespaces that are clearly stopped (they'd need starting first)
      for (const conn of connections) {
        if (conn.connectionState !== 'disconnected') continue;
        if (conn.serverInfo.Type === 'github' && conn.serverInfo.State === 'Stopped') continue;
        console.info(`[store] Auto-connecting to ${conn.serverInfo.Name} (${conn.serverInfo.Id})`);
        get().connectServer(conn.serverInfo.Id).catch(err =>
          console.warn(`[store] Auto-connect to ${conn.serverInfo.Name} failed:`, err)
        );
      }
    } catch (err) {
      console.error('[store] Failed to load servers:', err);
    }
  },

  addServer: async (req) => {
    try {
      await api.addServer(req);
      await get().loadServers();
      set({ showAddServer: false });
    } catch (err) {
      console.error('Failed to add server:', err);
    }
  },

  removeServer: async (serverId) => {
    const conn = get().getConnection(serverId);
    if (conn) conn.hub.disconnect();
    try {
      await api.removeServer(serverId);
    } catch (err) {
      console.error('Failed to remove server:', err);
    }
    await get().loadServers();
    set({ editServerId: null });
  },

  startServer: async (serverId) => {
    try {
      await api.startServer(serverId);
      // SSE will push serversChanged events as the server transitions states
    } catch (err) {
      console.error('Failed to start server:', err);
    }
  },

  connectServer: async (serverId) => {
    console.info(`[store] connectServer: ${serverId}`);
    const state = get();
    const conn = state.serverConnections.find(c => c.serverInfo.Id === serverId);
    if (!conn) { console.warn(`[store] connectServer: no connection found for ${serverId}`); return; }

    const updateConn = (updates: Partial<ServerConnection>) => {
      set(state => {
        const connections = state.serverConnections.map(c =>
          c.serverInfo.Id === serverId ? { ...c, ...updates } : c
        );
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter);
        return { serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions };
      });
    };

    conn.hub.setCallbacks({
      onStateChanged: (connectionState) => updateConn({ connectionState }),
      onProjectCreated: (status) => {
        set(state => {
          const summary: ProjectSummary = {
            Id: status.Id, Name: status.Name, State: status.State,
            UpdatedAt: status.UpdatedAt, CurrentQuestion: status.CurrentQuestion,
            RootName: status.RootName, ProfileName: status.ProfileName,
          };
          const connections = state.serverConnections.map(c =>
            c.serverInfo.Id === serverId
              ? { ...c, projects: [...c.projects, summary] }
              : c
          );
          const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter);
          const total = computeTotalWaiting(connections, state.projectQuestions, state.dismissedProjects);
          return { serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions, totalWaitingCount: total };
        });
      },
      onProjectDeleted: (projectId) => {
        set(state => {
          const connections = state.serverConnections.map(c =>
            c.serverInfo.Id === serverId
              ? { ...c, projects: c.projects.filter(p => p.Id !== projectId) }
              : c
          );
          const sel = state.selectedProject;
          const clearSel = sel?.serverId === serverId && sel?.projectId === projectId;
          const pq = { ...state.projectQuestions };
          delete pq[projectId];
          const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter);
          const total = computeTotalWaiting(connections, pq, state.dismissedProjects);
          return {
            serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions,
            projectQuestions: pq, totalWaitingCount: total,
            ...(clearSel ? { selectedProject: null, outputMessages: [], question: emptyQuestion } : {}),
          };
        });
      },
      onStatusChanged: (_projectId, status) => {
        set(state => {
          const connections = state.serverConnections.map(c =>
            c.serverInfo.Id === serverId
              ? { ...c, projects: c.projects.map(p => p.Id === status.Id
                  ? { ...p, State: status.State, UpdatedAt: status.UpdatedAt, CurrentQuestion: status.CurrentQuestion }
                  : p) }
              : c
          );
          const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter);

          const sel = state.selectedProject;
          let questionUpdate: Partial<AppState> = {};
          let pq = state.projectQuestions;
          if (sel?.serverId === serverId && sel?.projectId === status.Id && !state.dismissedProjects[status.Id]) {
            const isWaitingOrIdle = status.State === 'WaitingInput' || status.State === 'Idle';
            if (isWaitingOrIdle) {
              const project = connections.find(c => c.serverInfo.Id === serverId)?.projects.find(p => p.Id === status.Id);
              if (project) {
                const detected = detectQuestionFromStatus(
                  status.State, status.CurrentQuestion, project.Name,
                  state.question, state.lastInputSentAt, state.outputMessages,
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

          let dp = state.dismissedProjects;
          if (status.State === 'Running') {
            pq = { ...pq, [status.Id]: false };
            dp = { ...dp, [status.Id]: false };
            saveDismissed(dp);
          }

          const total = computeTotalWaiting(connections, pq, dp);
          return {
            serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions,
            projectQuestions: pq, dismissedProjects: dp, totalWaitingCount: total,
            ...questionUpdate,
          };
        });
      },
      onOutputReceived: (projectId, message) => {
        const s = get();
        const sel = s.selectedProject;
        const isDismissed = s.dismissedProjects[projectId];
        const isQuestion = !isDismissed && (
          message.isQuestion ||
          (message.type === 'assistant' && message.contentSummary && looksLikeQuestion(message.contentSummary))
        );

        if (isQuestion) {
          set(state => {
            const pq = { ...state.projectQuestions, [projectId]: true };
            const total = computeTotalWaiting(state.serverConnections, pq, state.dismissedProjects);
            return { projectQuestions: pq, totalWaitingCount: total };
          });
        }

        if (sel?.serverId === serverId && sel?.projectId === projectId) {
          const detected = detectQuestionFromMessage(message, s.question, s.lastInputSentAt, s.dismissedProjects[projectId]);
          set(state => ({
            outputMessages: [...state.outputMessages, message],
            ...(detected ? { question: detected } : {}),
          }));
        }

        if (s.isTileView) {
          set(state => ({
            tileMessages: {
              ...state.tileMessages,
              [projectId]: [...(state.tileMessages[projectId] ?? []), message],
            },
          }));
        }
      },
      onCreationProgress: () => {},
    });

    try {
      const hubUrl = api.getHubUrl(serverId);
      const hubOptions = api.getHubOptions(serverId);
      console.info(`[store] connectServer: hub.connect(${hubUrl})...`);
      await conn.hub.connect(hubUrl, hubOptions);
      console.info(`[store] connectServer: connected, refreshing projects...`);
      await get().refreshProjects(serverId);
      console.info(`[store] connectServer: ${serverId} ready`);
    } catch (err) {
      console.error(`[store] connectServer ${serverId} failed:`, err);
      updateConn({ connectionState: 'disconnected' });
    }
  },

  disconnectServer: async (serverId) => {
    const conn = get().getConnection(serverId);
    if (conn) {
      await conn.hub.disconnect();
      set(state => {
        const connections = state.serverConnections.map(c =>
          c.serverInfo.Id === serverId
            ? { ...c, projects: [], roots: [], profiles: [], connectionState: 'disconnected' as ConnectionState }
            : c
        );
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter);
        const total = computeTotalWaiting(connections, state.projectQuestions, state.dismissedProjects);
        return { serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions, totalWaitingCount: total };
      });
    }
  },

  refreshProjects: async (serverId) => {
    const conn = get().getConnection(serverId);
    if (!conn || conn.connectionState !== 'connected') return;
    try {
      console.info(`[store] refreshProjects: ${serverId}`);
      const [projects, roots, profiles] = await Promise.all([
        conn.hub.listProjects(),
        conn.hub.listProjectRoots(),
        conn.hub.listProfiles(),
      ]);
      console.info(`[store] refreshProjects: ${serverId} -> ${projects.length} projects, ${roots.length} roots, ${profiles.length} profiles`);
      if (projects.length > 0) console.debug('[store] projects sample:', JSON.stringify(projects[0]));
      set(state => {
        const connections = state.serverConnections.map(c =>
          c.serverInfo.Id === serverId ? { ...c, projects, roots, profiles } : c
        );
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter);
        const total = computeTotalWaiting(connections, state.projectQuestions, state.dismissedProjects);
        return { serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions, totalWaitingCount: total };
      });
    } catch (err) {
      console.error('Failed to refresh projects:', err);
    }
  },

  // ── Selection ─────────────────────────────────────────────

  selectedProject: null,
  selectProject: (serverId, projectId) => set({ selectedProject: { serverId, projectId }, outputMessages: [], question: emptyQuestion }),
  clearSelection: () => set({ selectedProject: null, outputMessages: [], question: emptyQuestion }),

  // ── Output ────────────────────────────────────────────────

  outputMessages: [],
  appendOutput: (_projectId, message) => set(state => ({ outputMessages: [...state.outputMessages, message] })),
  clearOutput: () => set({ outputMessages: [] }),

  // ── Questions ─────────────────────────────────────────────

  question: emptyQuestion,
  lastInputSentAt: 0,
  setQuestion: (q) => set({ question: q }),
  dismissQuestion: () => set(state => {
    const sel = state.selectedProject;
    const pq = sel ? { ...state.projectQuestions, [sel.projectId]: false } : state.projectQuestions;
    const dp = sel ? { ...state.dismissedProjects, [sel.projectId]: true } : state.dismissedProjects;
    saveDismissed(dp);
    const total = computeTotalWaiting(state.serverConnections, pq, dp);
    return { question: emptyQuestion, lastInputSentAt: Date.now(), projectQuestions: pq, dismissedProjects: dp, totalWaitingCount: total };
  }),
  markInputSent: () => set(state => {
    const sel = state.selectedProject;
    const pq = sel ? { ...state.projectQuestions, [sel.projectId]: false } : state.projectQuestions;
    const dp = sel ? { ...state.dismissedProjects, [sel.projectId]: false } : state.dismissedProjects;
    saveDismissed(dp);
    const total = computeTotalWaiting(state.serverConnections, pq, dp);
    return { question: emptyQuestion, lastInputSentAt: Date.now(), projectQuestions: pq, dismissedProjects: dp, totalWaitingCount: total };
  }),

  projectQuestions: {},
  dismissedProjects: loadDismissed(),
  totalWaitingCount: 0,

  // ── Tile view ─────────────────────────────────────────────

  isTileView: false,
  setTileView: (tile) => set({ isTileView: tile, tileMessages: {}, tileLoading: {}, selectedProject: null, outputMessages: [], question: emptyQuestion }),
  tileMessages: {},
  tileLoading: {},
  setTileLoading: (projectId, loading) => set(state => ({ tileLoading: { ...state.tileLoading, [projectId]: loading } })),
  clearTileMessages: () => set({ tileMessages: {}, tileLoading: {} }),

  // ── UI ────────────────────────────────────────────────────

  showAddServer: false,
  setShowAddServer: (show) => set({ showAddServer: show }),
  showCreateProject: false,
  createProjectContext: null,
  setShowCreateProject: (show, context) => set({ showCreateProject: show, createProjectContext: context ?? null }),
  editServerId: null,
  setEditServerId: (id) => set({ editServerId: id }),
  showRootManager: false,
  setShowRootManager: (show) => set({ showRootManager: show }),
  showProfileSettings: false,
  setShowProfileSettings: (show) => set({ showProfileSettings: show }),
}));
