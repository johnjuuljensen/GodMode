/**
 * Global app state using Zustand.
 * Matches Avalonia's data model: Profile → Root:Server → Projects.
 */
import { create } from 'zustand';
import { GodModeHub, type ConnectionState } from '../signalr/hub';
import type {
  ProjectSummary, ProjectRootInfo, ProfileInfo, ClaudeMessage,
  ServerInfo, CreateActionInfo, McpServerConfig,
} from '../signalr/types';
import type { GodModeChatEntry } from '../components/GodModeChat/GodModeChat';
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

export type SidebarGroupBy = 'profile' | 'root' | 'recent' | 'status';
const SIDEBAR_GROUP_ORDER: SidebarGroupBy[] = ['profile', 'root', 'recent', 'status'];

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
  flat?: boolean;        // true = render projects directly without root header
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

  // Sidebar grouping
  sidebarGroupBy: SidebarGroupBy;
  cycleSidebarGroupBy: () => void;

  // MCP cache: "serverId:profileName" -> MCP server names
  profileMcpCache: Record<string, Record<string, McpServerConfig>>;
  getProjectMcpServers: (serverId: string, profileName: string) => Record<string, McpServerConfig> | undefined;

  // Server lifecycle
  loadServers: () => Promise<void>;
  addServer: (req: AddServerRequest) => Promise<void>;
  removeServer: (serverId: string) => Promise<void>;
  connectServer: (serverId: string) => Promise<void>;
  disconnectServer: (serverId: string) => Promise<void>;
  startServer: (serverId: string) => Promise<void>;
  refreshProjects: (serverId: string) => Promise<void>;
  refreshFirstConnected: () => Promise<void>;

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
  showMcpConfig: boolean;
  setShowMcpConfig: (show: boolean) => void;
  showRootManager: boolean;
  setShowRootManager: (show: boolean) => void;
  showProfileSettings: boolean;
  setShowProfileSettings: (show: boolean) => void;
  showAppSettings: boolean;
  setShowAppSettings: (show: boolean) => void;

  // GodMode chat
  showGodModeChat: boolean;
  setShowGodModeChat: (show: boolean) => void;
  godModeChatMessages: GodModeChatEntry[];
  godModeChatLoading: boolean;
  appendGodModeChatMessage: (entry: GodModeChatEntry) => void;
  clearGodModeChat: () => void;
  setGodModeChatLoading: (loading: boolean) => void;

  // Feature visibility
  featureRoots: boolean;
  featureMcp: boolean;
  featureProfiles: boolean;
  setFeatureFlag: (flag: 'featureRoots' | 'featureMcp' | 'featureProfiles', value: boolean) => void;
}

// ── Helper: rebuild profile hierarchy ──────────────────────────

interface HierarchyResult {
  profileGroups: ProfileGroup[];
  inactiveServers: ServerConnection[];
  profileFilterOptions: string[];
}

/** Collect all connected projects + roots, filtered by profile. */
function collectFilteredData(connections: ServerConnection[], filter: string) {
  const allProfileNames = new Set<string>();
  const multiServer = connections.filter(c => c.connectionState === 'connected').length > 1;
  const representedServerIds = new Set<string>();

  for (const conn of connections) {
    if (conn.connectionState !== 'connected') continue;
    for (const p of conn.profiles) allProfileNames.add(p.Name);
  }

  // Collect all roots with their projects, respecting profile filter
  const allRoots: { root: ProjectRootInfo; projects: ProjectSummary[]; conn: ServerConnection; profileName: string }[] = [];

  for (const conn of connections) {
    if (conn.connectionState !== 'connected') continue;
    const projectsByRoot = new Map<string, ProjectSummary[]>();
    for (const p of conn.projects) {
      const rn = p.RootName ?? 'default';
      if (!projectsByRoot.has(rn)) projectsByRoot.set(rn, []);
      projectsByRoot.get(rn)!.push(p);
    }
    for (const root of conn.roots) {
      const profileName = root.ProfileName ?? 'Default';
      allProfileNames.add(profileName);
      if (filter !== 'All' && profileName.toLowerCase() !== filter.toLowerCase()) continue;
      allRoots.push({ root, projects: projectsByRoot.get(root.Name) ?? [], conn, profileName });
      representedServerIds.add(conn.serverInfo.Id);
    }
  }

  const inactiveServers = connections.filter(c => !representedServerIds.has(c.serverInfo.Id));
  const profileFilterOptions = ['All', ...Array.from(allProfileNames).sort()];

  return { allRoots, multiServer, inactiveServers, profileFilterOptions };
}


function rebuildHierarchy(
  connections: ServerConnection[],
  filter: string,
  groupBy: SidebarGroupBy = 'profile',
): HierarchyResult {
  const { allRoots, multiServer, inactiveServers, profileFilterOptions } = collectFilteredData(connections, filter);

  if (groupBy === 'profile') return buildByProfile(allRoots, multiServer, inactiveServers, profileFilterOptions);
  if (groupBy === 'root') return buildByRoot(allRoots, multiServer, inactiveServers, profileFilterOptions);
  if (groupBy === 'recent') return buildByRecent(allRoots, multiServer, inactiveServers, profileFilterOptions);
  return buildByStatus(allRoots, multiServer, inactiveServers, profileFilterOptions);
}

type RootEntry = { root: ProjectRootInfo; projects: ProjectSummary[]; conn: ServerConnection; profileName: string };

function buildByProfile(
  allRoots: RootEntry[], _multiServer: boolean,
  inactiveServers: ServerConnection[], profileFilterOptions: string[],
): HierarchyResult {
  const dict = new Map<string, { projects: ProjectSummary[]; serverId: string }>();
  for (const { projects, conn, profileName } of allRoots) {
    if (!dict.has(profileName)) dict.set(profileName, { projects: [], serverId: conn.serverInfo.Id });
    const entry = dict.get(profileName)!;
    for (const p of projects) if (!entry.projects.some(ep => ep.Id === p.Id)) entry.projects.push(p);
  }
  const profileGroups = [...dict.entries()].sort(([a], [b]) => a.localeCompare(b))
    .map(([name, { projects, serverId }]) => ({
      name,
      rootGroups: [{
        name: '', rootName: '', profileName: name, serverId, serverName: '',
        projects, actions: [], flat: true,
      }],
      projectCount: projects.length,
    }));
  return { profileGroups, inactiveServers, profileFilterOptions };
}

function buildByRoot(
  allRoots: RootEntry[], _multiServer: boolean,
  inactiveServers: ServerConnection[], profileFilterOptions: string[],
): HierarchyResult {
  const dict = new Map<string, { projects: ProjectSummary[]; serverId: string }>();
  for (const { root, projects, conn } of allRoots) {
    const key = `${conn.serverInfo.Id}:${root.Name}`;
    if (!dict.has(key)) dict.set(key, { projects: [], serverId: conn.serverInfo.Id });
    const entry = dict.get(key)!;
    for (const p of projects) if (!entry.projects.some(ep => ep.Id === p.Id)) entry.projects.push(p);
  }
  const profileGroups = [...dict.entries()]
    .sort(([a], [b]) => a.split(':')[1].localeCompare(b.split(':')[1]))
    .map(([key, { projects, serverId }]) => ({
      name: key.split(':')[1],
      rootGroups: [{
        name: '', rootName: key.split(':')[1], profileName: '', serverId, serverName: '',
        projects, actions: [], flat: true,
      }],
      projectCount: projects.length,
    }));
  return { profileGroups, inactiveServers, profileFilterOptions };
}

function buildByRecent(
  allRoots: RootEntry[], _multiServer: boolean,
  inactiveServers: ServerConnection[], profileFilterOptions: string[],
): HierarchyResult {
  // Flat list of all projects sorted by UpdatedAt desc
  const allProjects: { project: ProjectSummary; conn: ServerConnection; profileName: string }[] = [];
  for (const { projects, conn, profileName } of allRoots) {
    for (const p of projects) allProjects.push({ project: p, conn, profileName });
  }
  allProjects.sort((a, b) => new Date(b.project.UpdatedAt).getTime() - new Date(a.project.UpdatedAt).getTime());

  if (allProjects.length === 0) return { profileGroups: [], inactiveServers, profileFilterOptions };

  // Single flat group
  const serverId = allProjects[0].conn.serverInfo.Id;
  const profileGroups: ProfileGroup[] = [{
    name: 'Recent',
    rootGroups: [{
      name: '', rootName: '', profileName: '', serverId, serverName: '',
      projects: allProjects.map(e => e.project), actions: [], flat: true,
    }],
    projectCount: allProjects.length,
  }];
  return { profileGroups, inactiveServers, profileFilterOptions };
}

const STATUS_ORDER: Record<string, number> = { Running: 0, WaitingInput: 1, Idle: 2, Error: 3, Stopped: 4 };

function buildByStatus(
  allRoots: RootEntry[], _multiServer: boolean,
  inactiveServers: ServerConnection[], profileFilterOptions: string[],
): HierarchyResult {
  const dict = new Map<string, ProjectSummary[]>();
  let firstServerId = '';
  for (const { projects, conn } of allRoots) {
    if (!firstServerId) firstServerId = conn.serverInfo.Id;
    for (const p of projects) {
      const status = String(p.State ?? 'Idle');
      if (!dict.has(status)) dict.set(status, []);
      dict.get(status)!.push(p);
    }
  }
  const profileGroups = [...dict.entries()]
    .sort(([a], [b]) => (STATUS_ORDER[a] ?? 99) - (STATUS_ORDER[b] ?? 99))
    .map(([status, projects]) => ({
      name: status,
      rootGroups: [{
        name: '', rootName: '', profileName: '', serverId: firstServerId, serverName: '',
        projects, actions: [], flat: true,
      }],
      projectCount: projects.length,
    }));
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

// Helper to persist sidebar groupBy
const GROUPBY_KEY = 'godmode-sidebar-groupby';
function loadGroupBy(): SidebarGroupBy {
  const v = localStorage.getItem(GROUPBY_KEY);
  return SIDEBAR_GROUP_ORDER.includes(v as SidebarGroupBy) ? v as SidebarGroupBy : 'profile';
}

export const useAppStore = create<AppState>((set, get) => ({
  serverConnections: [],
  profileGroups: [],
  inactiveServers: [],
  profileFilterOptions: ['All'],
  profileFilter: 'All',

  getConnection: (serverId) => get().serverConnections.find(c => c.serverInfo.Id === serverId),
  getHub: (serverId) => get().serverConnections.find(c => c.serverInfo.Id === serverId)?.hub,

  setProfileFilter: (filter) => {
    const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(get().serverConnections, filter, get().sidebarGroupBy);
    set({ profileFilter: filter, profileGroups, inactiveServers, profileFilterOptions });
  },

  // Sidebar grouping
  sidebarGroupBy: loadGroupBy(),
  cycleSidebarGroupBy: () => {
    const current = get().sidebarGroupBy;
    const idx = SIDEBAR_GROUP_ORDER.indexOf(current);
    const next = SIDEBAR_GROUP_ORDER[(idx + 1) % SIDEBAR_GROUP_ORDER.length];
    localStorage.setItem(GROUPBY_KEY, next);
    const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(get().serverConnections, get().profileFilter, next);
    set({ sidebarGroupBy: next, profileGroups, inactiveServers, profileFilterOptions });
  },

  // MCP cache
  profileMcpCache: {},
  getProjectMcpServers: (serverId, profileName) => get().profileMcpCache[`${serverId}:${profileName}`],

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

      const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, get().profileFilter, get().sidebarGroupBy);
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
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
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
          const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
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
          const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
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
          const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);

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
      onRootsChanged: () => {
        get().refreshProjects(serverId);
      },
      onProfilesChanged: () => {
        get().refreshProjects(serverId);
      },
      onChatResponse: (message) => {
        const entry: GodModeChatEntry = { role: 'server', message };
        set(state => ({
          godModeChatMessages: [...state.godModeChatMessages, entry],
          godModeChatLoading: message.Type === 'ToolCall', // still processing if tool call
        }));
      },
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
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
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
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
        const total = computeTotalWaiting(connections, state.projectQuestions, state.dismissedProjects);
        return { serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions, totalWaitingCount: total };
      });

      // Load MCP servers per unique profile (fire-and-forget, non-blocking)
      const profileRootMap = new Map<string, string>();
      for (const root of roots) {
        const pn = root.ProfileName ?? 'Default';
        if (!profileRootMap.has(pn)) profileRootMap.set(pn, root.Name);
      }
      for (const [profileName, rootName] of profileRootMap) {
        conn.hub.getEffectiveMcpServers(profileName, rootName).then(mcpServers => {
          set(state => ({
            profileMcpCache: { ...state.profileMcpCache, [`${serverId}:${profileName}`]: mcpServers },
          }));
        }).catch(() => { /* ignore MCP cache failures */ });
      }
    } catch (err) {
      console.error('Failed to refresh projects:', err);
    }
  },

  refreshFirstConnected: async () => {
    const conn = get().serverConnections.find(c => c.connectionState === 'connected');
    if (conn) await get().refreshProjects(conn.serverInfo.Id);
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
  showMcpConfig: false,
  setShowMcpConfig: (show) => set({ showMcpConfig: show }),
  showRootManager: false,
  setShowRootManager: (show) => set({ showRootManager: show }),
  showProfileSettings: false,
  setShowProfileSettings: (show) => set({ showProfileSettings: show }),
  showAppSettings: false,
  setShowAppSettings: (show) => set({ showAppSettings: show }),

  // GodMode chat
  showGodModeChat: false,
  setShowGodModeChat: (show) => set({ showGodModeChat: show }),
  godModeChatMessages: [],
  godModeChatLoading: false,
  appendGodModeChatMessage: (entry) => set(state => ({ godModeChatMessages: [...state.godModeChatMessages, entry] })),
  clearGodModeChat: () => set({ godModeChatMessages: [], godModeChatLoading: false }),
  setGodModeChatLoading: (loading) => set({ godModeChatLoading: loading }),

  // Feature visibility (persisted to localStorage)
  featureRoots: localStorage.getItem('godmode-feature-roots') !== 'false',
  featureMcp: localStorage.getItem('godmode-feature-mcp') !== 'false',
  featureProfiles: localStorage.getItem('godmode-feature-profiles') !== 'false',
  setFeatureFlag: (flag, value) => {
    localStorage.setItem(`godmode-${flag.replace('feature', 'feature-').toLowerCase()}`, String(value));
    set({ [flag]: value });
  },
}));
