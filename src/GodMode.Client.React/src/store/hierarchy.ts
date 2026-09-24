/**
 * The sidebar's grouping of every connected (or reconnecting) server's projects. Pure: rebuilt from the server
 * connections on each change. An item carries the server it is from, so a group may mix servers.
 */
import type { GodModeHub, ConnectionState } from '../signalr/hub';
import type { ProjectSummary, ProjectRootInfo, ProfileInfo, CreateActionInfo, ServerInfo } from '../signalr/types';
import { projectKey, type ProjectKey } from './projectKey';

export type SidebarGroupBy = 'profile' | 'root' | 'recent' | 'status';
export const SIDEBAR_GROUP_ORDER: SidebarGroupBy[] = ['profile', 'root', 'recent', 'status'];

export interface ServerConnection {
  serverInfo: ServerInfo;
  hub: GodModeHub;
  connectionState: ConnectionState;
  projects: ProjectSummary[];
  roots: ProjectRootInfo[];
  profiles: ProfileInfo[];
}

/** A project as the sidebar lists it: selecting it selects (serverId, project.Id). */
export interface SidebarItem {
  key: ProjectKey;
  serverId: string;
  project: ProjectSummary;
  /** The server's name, set when another connected server has a project of the same name. */
  serverLabel?: string;
}

export interface RootGroup {
  name: string;          // display name (qualified with server name if multi-server)
  rootName: string;      // actual root name for API calls
  profileName: string;
  /** The server of a root's group; absent on a flat group, whose items may come from several servers. */
  serverId?: string;
  serverName: string;
  items: SidebarItem[];
  actions: CreateActionInfo[];
  flat?: boolean;        // true = render projects directly without root header
}

export interface ProfileGroup {
  /** Unique among the groups: two servers' roots of one name are two groups. */
  key: string;
  name: string;
  rootGroups: RootGroup[];
  projectCount: number;
}

export interface HierarchyResult {
  profileGroups: ProfileGroup[];
  inactiveServers: ServerConnection[];
  profileFilterOptions: string[];
}

type RootEntry = { root: ProjectRootInfo; items: SidebarItem[]; conn: ServerConnection; profileName: string };

/** A server whose projects are shown: connected, or reconnecting (shown as it was until it is back). */
export const isListed = (c: ServerConnection) => c.connectionState === 'connected' || c.connectionState === 'reconnecting';

/** Collect all listed projects + roots, filtered by profile. A reconnecting server keeps its last lists. */
function collectFilteredData(connections: ServerConnection[], filter: string) {
  const allProfileNames = new Set<string>();
  const listed = connections.filter(isListed);
  const representedServerIds = new Set<string>();

  for (const conn of listed) {
    for (const p of conn.profiles) allProfileNames.add(p.Name);
  }

  // Collect all roots with their projects, respecting profile filter
  const allRoots: RootEntry[] = [];
  for (const conn of listed) {
    const serverId = conn.serverInfo.Id;
    const itemsByRoot = new Map<string, SidebarItem[]>();
    for (const p of conn.projects) {
      const rn = p.RootName ?? 'default';
      if (!itemsByRoot.has(rn)) itemsByRoot.set(rn, []);
      itemsByRoot.get(rn)!.push({ key: projectKey(serverId, p.Id), serverId, project: p });
    }
    for (const root of conn.roots) {
      const profileName = root.ProfileName ?? 'Default';
      allProfileNames.add(profileName);
      if (filter !== 'All' && profileName.toLowerCase() !== filter.toLowerCase()) continue;
      allRoots.push({ root, items: itemsByRoot.get(root.Name) ?? [], conn, profileName });
      representedServerIds.add(serverId);
    }
  }

  labelSharedNames(allRoots);
  const inactiveServers = connections.filter(c => !representedServerIds.has(c.serverInfo.Id));
  const profileFilterOptions = ['All', ...Array.from(allProfileNames).sort()];
  return { allRoots, inactiveServers, profileFilterOptions };
}

/** Labels each item whose project name is shown from more than one server with its server's name. */
function labelSharedNames(allRoots: RootEntry[]) {
  const serversByName = new Map<string, Set<string>>();
  for (const { items } of allRoots) {
    for (const i of items) {
      if (!serversByName.has(i.project.Name)) serversByName.set(i.project.Name, new Set());
      serversByName.get(i.project.Name)!.add(i.serverId);
    }
  }
  for (const { items, conn } of allRoots) {
    for (const i of items) {
      if (serversByName.get(i.project.Name)!.size > 1) i.serverLabel = conn.serverInfo.Name;
    }
  }
}

export function rebuildHierarchy(
  connections: ServerConnection[],
  filter: string,
  groupBy: SidebarGroupBy = 'profile',
): HierarchyResult {
  const { allRoots, inactiveServers, profileFilterOptions } = collectFilteredData(connections, filter);
  const build = { profile: buildByProfile, root: buildByRoot, recent: buildByRecent, status: buildByStatus }[groupBy];
  return { profileGroups: build(allRoots), inactiveServers, profileFilterOptions };
}

/** A group listing its items directly, with no root header. */
const flatGroup = (key: string, name: string, items: SidebarItem[], root?: { rootName: string; serverId: string }): ProfileGroup => ({
  key, name,
  rootGroups: [{
    name: '', rootName: root?.rootName ?? '', profileName: '', serverId: root?.serverId, serverName: '',
    items, actions: [], flat: true,
  }],
  projectCount: items.length,
});

/** Adds items to a group's list, once each. */
function addItems(target: SidebarItem[], items: SidebarItem[]) {
  for (const i of items) if (!target.some(t => t.key === i.key)) target.push(i);
}

function buildByProfile(allRoots: RootEntry[]): ProfileGroup[] {
  const dict = new Map<string, SidebarItem[]>();
  for (const { items, profileName } of allRoots) {
    if (!dict.has(profileName)) dict.set(profileName, []);
    addItems(dict.get(profileName)!, items);
  }
  return [...dict.entries()].sort(([a], [b]) => a.localeCompare(b))
    .map(([name, items]) => flatGroup(name, name, items));
}

function buildByRoot(allRoots: RootEntry[]): ProfileGroup[] {
  const dict = new Map<string, { rootName: string; conn: ServerConnection; items: SidebarItem[] }>();
  const serversByRoot = new Map<string, number>();
  for (const { root, items, conn } of allRoots) {
    const key = `${conn.serverInfo.Id}:${root.Name}`;
    if (!dict.has(key)) {
      dict.set(key, { rootName: root.Name, conn, items: [] });
      serversByRoot.set(root.Name, (serversByRoot.get(root.Name) ?? 0) + 1);
    }
    addItems(dict.get(key)!.items, items);
  }
  return [...dict.entries()]
    .sort(([, a], [, b]) => a.rootName.localeCompare(b.rootName) || a.conn.serverInfo.Name.localeCompare(b.conn.serverInfo.Name))
    .map(([key, { rootName, conn, items }]) => flatGroup(
      key,
      serversByRoot.get(rootName)! > 1 ? `${rootName} (${conn.serverInfo.Name})` : rootName,
      items,
      { rootName, serverId: conn.serverInfo.Id },
    ));
}

function buildByRecent(allRoots: RootEntry[]): ProfileGroup[] {
  // Flat list of all projects sorted by UpdatedAt desc
  const items = allRoots.flatMap(r => r.items)
    .sort((a, b) => new Date(b.project.UpdatedAt).getTime() - new Date(a.project.UpdatedAt).getTime());
  return items.length === 0 ? [] : [flatGroup('Recent', 'Recent', items)];
}

const STATUS_ORDER: Record<string, number> = { Running: 0, WaitingPermission: 1, WaitingInput: 1, Idle: 2, Error: 3, Stopped: 4 };

function buildByStatus(allRoots: RootEntry[]): ProfileGroup[] {
  const dict = new Map<string, SidebarItem[]>();
  for (const i of allRoots.flatMap(r => r.items)) {
    const status = String(i.project.State ?? 'Idle');
    if (!dict.has(status)) dict.set(status, []);
    dict.get(status)!.push(i);
  }
  return [...dict.entries()]
    .sort(([a], [b]) => (STATUS_ORDER[a] ?? 99) - (STATUS_ORDER[b] ?? 99))
    .map(([status, items]) => flatGroup(status, status, items));
}

export function computeTotalWaiting(
  connections: ServerConnection[], pq: Record<ProjectKey, boolean>, dp: Record<ProjectKey, true>,
): number {
  let total = 0;
  for (const conn of connections) {
    for (const p of conn.projects) {
      const key = projectKey(conn.serverInfo.Id, p.Id);
      // A permission prompt cannot be dismissed: claude waits until it is answered
      if (p.State === 'WaitingPermission' || (!dp[key] && (p.State === 'WaitingInput' || pq[key]))) total++;
    }
  }
  return total;
}
