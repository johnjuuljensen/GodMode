/**
 * The sidebar's grouping of every connected (or reconnecting) server's projects. Pure: rebuilt from the server
 * connections on each change. Always a group per profile, whatever the group-by; an item carries the server it
 * is from, so a profile's group may mix servers.
 */
import type { GodModeHub, ConnectionState } from '../signalr/hub';
import type { ProjectSummary, ProjectRootInfo, ProfileInfo, ServerInfo } from '../signalr/types';
import { projectKey, type ProjectKey } from './projectKey';

/** What the sidebar lists under each profile: the profile is always the top level (#308). */
export type SidebarGroupBy = 'root' | 'recent' | 'status';
export const SIDEBAR_GROUP_ORDER: SidebarGroupBy[] = ['root', 'recent', 'status'];
export const DEFAULT_GROUP_BY: SidebarGroupBy = 'root';

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
  /** Whether the root's header offers +: the root has create actions and its server is connected. */
  canCreate: boolean;
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

/** A root is named within its profile: one server may have a root of one name in two profiles. */
const rootKey = (profileName: string | null | undefined, rootName: string) => `${profileName ?? 'Default'}/${rootName}`;

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
      const rn = rootKey(p.ProfileName, p.RootName ?? 'default');
      if (!itemsByRoot.has(rn)) itemsByRoot.set(rn, []);
      itemsByRoot.get(rn)!.push({ key: projectKey(serverId, p.Id), serverId, project: p });
    }
    for (const root of conn.roots) {
      const profileName = root.ProfileName ?? 'Default';
      allProfileNames.add(profileName);
      if (filter !== 'All' && profileName.toLowerCase() !== filter.toLowerCase()) continue;
      allRoots.push({ root, items: itemsByRoot.get(rootKey(root.ProfileName, root.Name)) ?? [], conn, profileName });
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
  groupBy: SidebarGroupBy = DEFAULT_GROUP_BY,
): HierarchyResult {
  const { allRoots, inactiveServers, profileFilterOptions } = collectFilteredData(connections, filter);
  const build = { root: rootGroupsOf, recent: recentGroupOf, status: statusGroupOf }[groupBy];
  return { profileGroups: byProfile(allRoots, build), inactiveServers, profileFilterOptions };
}

/**
 * One group per profile name, sorted, whatever servers it is on: a profile never mixes with another (#308).
 * `build` chooses what is under each profile, from that profile's roots.
 */
function byProfile(allRoots: RootEntry[], build: (roots: RootEntry[]) => RootGroup[]): ProfileGroup[] {
  const dict = new Map<string, RootEntry[]>();
  for (const r of allRoots) {
    if (!dict.has(r.profileName)) dict.set(r.profileName, []);
    dict.get(r.profileName)!.push(r);
  }
  return [...dict.entries()].sort(([a], [b]) => a.localeCompare(b))
    .map(([name, roots]) => {
      const rootGroups = build(roots);
      return { key: name, name, rootGroups, projectCount: rootGroups.reduce((n, rg) => n + rg.items.length, 0) };
    });
}

/** A profile's projects listed directly, with no root header. */
const flatGroup = (profileName: string, items: SidebarItem[]): RootGroup => ({
  name: '', rootName: '', profileName, serverName: '', items, canCreate: false, flat: true,
});

/** A root header per server, each with its +, then that root's projects. */
function rootGroupsOf(roots: RootEntry[]): RootGroup[] {
  const serversByRoot = new Map<string, number>();
  for (const { root } of roots) serversByRoot.set(root.Name, (serversByRoot.get(root.Name) ?? 0) + 1);
  return [...roots]
    .sort((a, b) => a.root.Name.localeCompare(b.root.Name) || a.conn.serverInfo.Name.localeCompare(b.conn.serverInfo.Name))
    .map(({ root, items, conn, profileName }) => ({
      name: serversByRoot.get(root.Name)! > 1 ? `${root.Name} (${conn.serverInfo.Name})` : root.Name,
      rootName: root.Name,
      profileName,
      serverId: conn.serverInfo.Id,
      serverName: conn.serverInfo.Name,
      items,
      canCreate: (root.Actions?.length ?? 0) > 0 && conn.connectionState === 'connected',
    }));
}

const byUpdatedDesc = (a: SidebarItem, b: SidebarItem) =>
  new Date(b.project.UpdatedAt).getTime() - new Date(a.project.UpdatedAt).getTime();

function recentGroupOf(roots: RootEntry[]): RootGroup[] {
  const items = roots.flatMap(r => r.items).sort(byUpdatedDesc);
  return [flatGroup(roots[0].profileName, items)];
}

const STATUS_ORDER: Record<string, number> = { Running: 0, WaitingPermission: 1, WaitingInput: 1, Idle: 2, Error: 3, Stopped: 4 };
const statusRank = (i: SidebarItem) => STATUS_ORDER[String(i.project.State ?? 'Idle')] ?? 99;

/** A profile's projects by status, the most recent first within one. */
function statusGroupOf(roots: RootEntry[]): RootGroup[] {
  const items = roots.flatMap(r => r.items).sort((a, b) => statusRank(a) - statusRank(b) || byUpdatedDesc(a, b));
  return [flatGroup(roots[0].profileName, items)];
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
