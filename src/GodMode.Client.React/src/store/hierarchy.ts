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
  /** Its action is transient (CreateActionInfo.Transient), as its root lists it: it folds sooner. */
  transient?: boolean;
  /** The sessions it started, nested under it (#390), in the order its profile lists them. */
  children: SidebarItem[];
  /**
   * Its parent's name, when its parent is listed but it is not nested under it: in another profile, which never
   * mixes (#308), or in another root with 'stay'.
   */
  startedBy?: string;
  /** Its own root, when it is nested under a parent in another root: as it is shown (its title, else its name). */
  ownRoot?: string;
  /** Its root as it is shown (#434): the root's title, else its name. */
  rootShown?: string;
}

/**
 * Where a child in another root of its parent's profile is shown (#390): under its parent, marked with its own
 * root ('nest', the default: an overseer's fleet reads as one piece of work, wherever its workers' roots are),
 * or at the top of its own root, noting its parent ('stay').
 */
export type OtherRootChildren = 'nest' | 'stay';
export const DEFAULT_OTHER_ROOT_CHILDREN: OtherRootChildren = 'nest';

export interface RootGroup {
  name: string;          // display name: its title, else its name (qualified with the name on a clash, the server if multi-server)
  rootName: string;      // actual root name for API calls
  /** The root's name, when it is shown by its title (#434): its header's tooltip. */
  tooltip?: string;
  profileName: string;
  /** The server of a root's group; absent on a flat group, whose items may come from several servers. */
  serverId?: string;
  serverName: string;
  /** Its top-level sessions, each with what it started under it. */
  items: SidebarItem[];
  /** How many sessions the root has, wherever they are shown: one nested under a parent in another root is still its. */
  sessionCount: number;
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

/**
 * A profile is a name, across every server, and matched without case (#308): a window locked to one (#340)
 * shows that name's roots on every server. What names none is in Default.
 */
export const profileNameOf = (name: string | null | undefined) => name ?? 'Default';
export const sameProfile = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/** What a root is shown as (#434): its title, else its name, which stays its key. */
export const rootShownOf = (root: ProjectRootInfo) => root.Title ?? root.Name;

/** What the root of that name in that profile is shown as, among a server's roots: its name when it is not listed. */
export const rootShown = (roots: readonly ProjectRootInfo[] | undefined, profileName: string | null | undefined, rootName: string) => {
  const root = roots?.find(r => r.Name === rootName && sameProfile(profileNameOf(r.ProfileName), profileNameOf(profileName)));
  return root ? rootShownOf(root) : rootName;
};
/** Whether a profile passes a filter: 'All', or the one it names. */
export const inProfile = (name: string | null | undefined, filter: string) =>
  filter === 'All' || sameProfile(profileNameOf(name), filter);

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
      itemsByRoot.get(rn)!.push({ key: projectKey(serverId, p.Id), serverId, project: p, children: [] });
    }
    // A project whose root the server no longer lists (removed while its claude runs, or moved to
    // another profile until it stops) is still shown, under its root's name, with no +
    const listedRoots = new Set(conn.roots.map(root => rootKey(root.ProfileName, root.Name)));
    const unlisted: ProjectRootInfo[] = [...itemsByRoot.entries()]
      .filter(([key]) => !listedRoots.has(key))
      .map(([, [{ project }]]) => ({ Name: project.RootName ?? 'default', ProfileName: project.ProfileName ?? 'Default', Actions: [] }));
    for (const root of [...conn.roots, ...unlisted]) {
      const profileName = profileNameOf(root.ProfileName);
      allProfileNames.add(profileName);
      if (!inProfile(profileName, filter)) continue;
      const items = itemsByRoot.get(rootKey(root.ProfileName, root.Name)) ?? [];
      const transient = new Set((root.Actions ?? []).filter(a => a.Transient).map(a => a.Name));
      for (const item of items) {
        item.transient = item.project.ActionName != null && transient.has(item.project.ActionName);
        item.rootShown = rootShownOf(root);
      }
      allRoots.push({ root, items, conn, profileName });
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
  otherRoot: OtherRootChildren = DEFAULT_OTHER_ROOT_CHILDREN,
): HierarchyResult {
  const { allRoots, inactiveServers, profileFilterOptions } = collectFilteredData(connections, filter);
  const build = { root: rootGroupsOf, recent: recentGroupOf, status: statusGroupOf }[groupBy];
  // Every listed session, in the profile filter or not: a parent filtered out still names its child's note
  const names = new Map(connections.filter(isListed)
    .flatMap(c => c.projects.map(p => [projectKey(c.serverInfo.Id, p.Id), p.Name] as const)));
  return { profileGroups: byProfile(allRoots, build, otherRoot, names), inactiveServers, profileFilterOptions };
}

/**
 * One group per profile name, sorted, whatever servers it is on: a profile never mixes with another (#308).
 * `build` chooses what is under each profile, from that profile's roots; then each session nests under its parent.
 */
function byProfile(
  allRoots: RootEntry[], build: (roots: RootEntry[]) => RootGroup[],
  otherRoot: OtherRootChildren, names: ReadonlyMap<ProjectKey, string>,
): ProfileGroup[] {
  const dict = new Map<string, RootEntry[]>();
  for (const r of allRoots) {
    if (!dict.has(r.profileName)) dict.set(r.profileName, []);
    dict.get(r.profileName)!.push(r);
  }
  return [...dict.entries()].sort(([a], [b]) => a.localeCompare(b))
    .map(([name, roots]) => {
      const rootGroups = build(roots);
      const projectCount = rootGroups.reduce((n, rg) => n + rg.items.length, 0);
      nest(rootGroups, otherRoot, names);
      return { key: name, name, rootGroups, projectCount };
    });
}

/** The key of the session that started the item: on its own server, as no session starts one on another. */
const parentKeyOf = (item: SidebarItem) => item.project.ParentId ? projectKey(item.serverId, item.project.ParentId) : null;

/**
 * Nests a profile's sessions under their parents (#390), on as many levels as there are. A session is at the
 * top when its parent is not in the profile's list (gone; in another profile, or another root with 'stay',
 * which it notes), and when it is its own ancestor: a self-parent or a cycle, planted in status.json, never
 * loops. What hangs off a cycle nests under it as usual.
 */
function nest(groups: RootGroup[], otherRoot: OtherRootChildren, names: ReadonlyMap<ProjectKey, string>) {
  const groupOf = new Map<ProjectKey, RootGroup>();
  const byKey = new Map<ProjectKey, SidebarItem>();
  for (const g of groups) {
    for (const i of g.items) { groupOf.set(i.key, g); byKey.set(i.key, i); }
  }
  const sameRoot = (a: SidebarItem, b: SidebarItem) => a.project.RootName === b.project.RootName;
  // The parent it may sit under, before cycles are broken
  const candidate = (i: SidebarItem) => {
    const key = parentKeyOf(i);
    const parent = key ? byKey.get(key) : undefined;
    return parent && (otherRoot === 'nest' || sameRoot(i, parent)) ? parent : undefined;
  };
  // Whether the item is reached again from its parent. Each step is to an item not seen yet, so it ends
  const onCycle = (i: SidebarItem) => {
    const seen = new Set<SidebarItem>();
    for (let p = candidate(i); p && !seen.has(p); p = candidate(p)) {
      if (p === i) return true;
      seen.add(p);
    }
    return false;
  };

  const all = groups.flatMap(g => g.items);
  const parents = new Map<SidebarItem, SidebarItem>();
  for (const i of all) {
    i.children = [];
    const parent = candidate(i);
    const key = parentKeyOf(i);
    if (parent) {
      // A cycle's members are at the top, with no note
      if (!onCycle(i)) parents.set(i, parent);
    } else if (key) {
      i.startedBy = names.get(key);
    }
  }
  for (const i of all) {
    const parent = parents.get(i);
    if (!parent) continue;
    parent.children.push(i);
    if (!groupOf.get(parent.key)!.flat && !sameRoot(i, parent)) i.ownRoot = i.rootShown ?? i.project.RootName ?? undefined;
  }
  for (const g of groups) g.items = g.items.filter(i => !parents.has(i));
}

// ── Folded headers (#427): kept on this device, by keys that stay the same whatever other servers are listed ──

/** A profile's header, by its name, matched without case as profiles are (#308): one fold on every server. */
export const profileFoldKey = (profileName: string) => `profile:${profileName.toLowerCase()}`;
/** The start of every root fold key of a server: its roots are its own. */
export const rootFoldPrefix = (serverId: string) => `root:${serverId}:`;
/** A root's header, by server, profile and root: the root of one name on two servers, or in two profiles, is two. */
export const rootFoldKey = (serverId: string, profileName: string, rootName: string) =>
  `${rootFoldPrefix(serverId)}${rootKey(profileName, rootName)}`;
/** The inactive servers' section. */
export const INACTIVE_FOLD_KEY = 'inactive';

/** What is folded above a session: the headers and the sessions to open to show its row. */
export interface FoldPath { headers: string[]; sessions: ProjectKey[] }

/** The folds above the session, from its profile down, or null when the list does not show it. */
export function foldPathOf(profileGroups: ProfileGroup[], key: ProjectKey): FoldPath | null {
  const find = (items: SidebarItem[], above: ProjectKey[]): ProjectKey[] | null => {
    for (const item of items) {
      if (item.key === key) return above;
      const found = find(item.children, [...above, item.key]);
      if (found) return found;
    }
    return null;
  };
  for (const group of profileGroups) {
    for (const rg of group.rootGroups) {
      const sessions = find(rg.items, []);
      if (!sessions) continue;
      const headers = [profileFoldKey(group.name)];
      if (!rg.flat && rg.serverId) headers.push(rootFoldKey(rg.serverId, rg.profileName, rg.rootName));
      return { headers, sessions };
    }
  }
  return null;
}

/** A profile's projects listed directly, with no root header. */
const flatGroup = (profileName: string, items: SidebarItem[]): RootGroup => ({
  name: '', rootName: '', profileName, serverName: '', items, sessionCount: items.length, canCreate: false, flat: true,
});

/**
 * A root header per server, each with its +, then that root's projects. A root is shown by its title (#434), with its
 * name when another root here is shown as that title too, and with its server when another server has a root of its name.
 */
function rootGroupsOf(roots: RootEntry[]): RootGroup[] {
  const serversByRoot = new Map<string, number>();
  for (const { root } of roots) serversByRoot.set(root.Name, (serversByRoot.get(root.Name) ?? 0) + 1);
  const namesByShown = new Map<string, Set<string>>();
  for (const { root } of roots) namesByShown.set(rootShownOf(root), (namesByShown.get(rootShownOf(root)) ?? new Set()).add(root.Name));
  const nameOf = (root: ProjectRootInfo) =>
    root.Title != null && namesByShown.get(root.Title)!.size > 1 ? `${root.Title} (${root.Name})` : rootShownOf(root);
  return [...roots]
    .sort((a, b) => nameOf(a.root).localeCompare(nameOf(b.root)) || a.conn.serverInfo.Name.localeCompare(b.conn.serverInfo.Name))
    .map(({ root, items, conn, profileName }) => ({
      name: serversByRoot.get(root.Name)! > 1 ? `${nameOf(root)} (${conn.serverInfo.Name})` : nameOf(root),
      tooltip: root.Title != null ? root.Name : undefined,
      rootName: root.Name,
      profileName,
      serverId: conn.serverInfo.Id,
      serverName: conn.serverInfo.Name,
      items,
      sessionCount: items.length,
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

// ── Folding (#325): a root with many sessions stays readable. Nothing is deleted by it ──

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * How long a session may have been idle before it folds under "N older": a week, so the list is this
 * week's work, and a day for a transient action's (a chat, an experiment), which is yesterday's once it
 * has gone quiet. Its last activity is UpdatedAt, which every change of state moves.
 */
export const FOLD_AFTER_MS = 7 * DAY_MS;
export const TRANSIENT_FOLD_AFTER_MS = 1 * DAY_MS;

/** A session with a claude (working, waiting on the user, or idle between turns) never folds, however old. */
const LIVE_STATES = new Set(['Running', 'WaitingInput', 'WaitingPermission', 'Idle']);

/**
 * Whether the item folds at `now`: no claude, nothing the caller keeps it for (it needs the user, it is open),
 * and quiet long enough. A parent folds with what it started, and only when every one of them folds too (#390).
 */
export function folds(item: SidebarItem, now: number, keep: (item: SidebarItem) => boolean): boolean {
  if (LIVE_STATES.has(String(item.project.State ?? 'Idle')) || keep(item)) return false;
  const quiet = now - new Date(item.project.UpdatedAt).getTime();
  return quiet > (item.transient ? TRANSIENT_FOLD_AFTER_MS : FOLD_AFTER_MS)
    && item.children.every(c => folds(c, now, keep));
}

/** Every session nested under the item, on every level. */
export const descendantsOf = (item: SidebarItem): SidebarItem[] =>
  item.children.flatMap(c => [c, ...descendantsOf(c)]);

/** A group's items split in two, each in the order it had: those shown, and those folded under "N older". */
export function foldItems(items: SidebarItem[], now: number, keep: (item: SidebarItem) => boolean): { shown: SidebarItem[]; older: SidebarItem[] } {
  const shown: SidebarItem[] = [];
  const older: SidebarItem[] = [];
  for (const item of items) (folds(item, now, keep) ? older : shown).push(item);
  return { shown, older };
}
