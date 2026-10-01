/**
 * The left list is always grouped by profile, and the group-by chooses what is under each (#308): root
 * headers with their + for Root, the profile's projects directly for Recent and Status.
 */
import { describe, expect, it } from 'vitest';
import type { ConnectionState, GodModeHub } from '../signalr/hub';
import type { ProjectRootInfo, ProjectState, ProjectSummary } from '../signalr/types';
import { rebuildHierarchy, type OtherRootChildren, type ServerConnection, type SidebarGroupBy, type SidebarItem } from './hierarchy';

const issue = [{ Name: 'issue', AllowSkipPermissions: false, Session: true, Transient: false }];

const rootOf = (name: string, profile: string, actions = issue): ProjectRootInfo => ({ Name: name, ProfileName: profile, Actions: actions });

const projectOf = (id: string, profile: string, root: string, state: ProjectState, updatedAt: string): ProjectSummary =>
  ({ Id: `${profile}/${root}/${id}`, Name: id, State: state, UpdatedAt: updatedAt, RootName: root, ProfileName: profile, SharedFolder: false, Adopted: false });

const server = (id: string, roots: ProjectRootInfo[], projects: ProjectSummary[], connectionState: ConnectionState = 'connected'): ServerConnection => ({
  serverInfo: { Id: id, Name: `Server ${id}` } as ServerConnection['serverInfo'],
  hub: {} as GodModeHub,
  connectionState,
  projects,
  roots,
  profiles: [...new Set(roots.map(r => r.ProfileName!))].map(Name => ({ Name })),
});

/** Server A: profile "GodMode dev" with roots GodMode and tools, profile "VoiceBot dev" with root voicebot. */
const serverA = () => server('A', [rootOf('GodMode', 'GodMode dev'), rootOf('tools', 'GodMode dev', []), rootOf('voicebot', 'VoiceBot dev')], [
  projectOf('feat-296', 'GodMode dev', 'GodMode', 'Idle', '2026-09-28T09:00:00Z'),
  projectOf('bug-300', 'GodMode dev', 'GodMode', 'Running', '2026-09-28T08:00:00Z'),
  projectOf('lint', 'GodMode dev', 'tools', 'WaitingInput', '2026-09-28T10:00:00Z'),
  projectOf('feat-44', 'VoiceBot dev', 'voicebot', 'Idle', '2026-09-28T07:00:00Z'),
]);

/** What the list shows, as text: `profile (count)`, `  root [+]` headers and `    project` items. */
function outline(connections: ServerConnection[], groupBy: SidebarGroupBy, filter = 'All'): string[] {
  return rebuildHierarchy(connections, filter, groupBy).profileGroups.flatMap(g => [
    `${g.name} (${g.projectCount})`,
    ...g.rootGroups.flatMap(rg => [
      ...(rg.flat ? [] : [`  ${rg.name}${rg.canCreate ? ' +' : ''}`]),
      ...rg.items.map(i => `    ${i.serverId}:${i.project.Name}`),
    ]),
  ]);
}

describe('by root', () => {
  it('lists profile, then root headers with their +, then that root\'s projects', () => {
    expect(outline([serverA()], 'root')).toEqual([
      'GodMode dev (3)',
      '  GodMode +',
      '    A:feat-296',
      '    A:bug-300',
      '  tools',
      '    A:lint',
      'VoiceBot dev (1)',
      '  voicebot +',
      '    A:feat-44',
    ]);
  });

  it("gives a root's + its server and root", () => {
    const [rg] = rebuildHierarchy([serverA()], 'All', 'root').profileGroups[0].rootGroups;
    expect(rg).toMatchObject({ serverId: 'A', rootName: 'GodMode', canCreate: true });
  });

  it('offers no + on a server that is reconnecting, whose projects are still shown', () => {
    expect(outline([server('A', [rootOf('GodMode', 'GodMode dev')], [], 'reconnecting')], 'root'))
      .toEqual(['GodMode dev (0)', '  GodMode']);
  });

  it('keeps a root of one name in two profiles of one server apart', () => {
    const conn = server('A', [rootOf('work', 'P1'), rootOf('work', 'P2')], [
      projectOf('one', 'P1', 'work', 'Idle', '2026-09-28T09:00:00Z'),
      projectOf('two', 'P2', 'work', 'Idle', '2026-09-28T09:00:00Z'),
    ]);
    expect(outline([conn], 'root')).toEqual(['P1 (1)', '  work +', '    A:one', 'P2 (1)', '  work +', '    A:two']);
  });
});

describe.each<SidebarGroupBy>(['recent', 'status'])('by %s', groupBy => {
  it('lists each profile with a header over its own projects, and no root headers', () => {
    const shown = outline([serverA()], groupBy);
    expect(shown.filter(l => !l.startsWith('    '))).toEqual(['GodMode dev (3)', 'VoiceBot dev (1)']);
    expect(shown.slice(shown.indexOf('VoiceBot dev (1)'))).toEqual(['VoiceBot dev (1)', '    A:feat-44']);
  });
});

describe('the order under a profile', () => {
  it('is most recent first by recent', () => {
    expect(outline([serverA()], 'recent').slice(0, 4)).toEqual(['GodMode dev (3)', '    A:lint', '    A:feat-296', '    A:bug-300']);
  });

  it('is by status, then most recent, by status', () => {
    expect(outline([serverA()], 'status').slice(0, 4)).toEqual(['GodMode dev (3)', '    A:bug-300', '    A:lint', '    A:feat-296']);
  });
});

it('has a header for a profile even when it is the only one', () => {
  expect(outline([serverA()], 'recent', 'VoiceBot dev')).toEqual(['VoiceBot dev (1)', '    A:feat-44']);
});

describe('two servers sharing a profile name', () => {
  const serverB = () => server('B', [rootOf('GodMode', 'GodMode dev'), rootOf('site', 'GodMode dev')], [
    projectOf('feat-296', 'GodMode dev', 'GodMode', 'Idle', '2026-09-28T11:00:00Z'),
    projectOf('docs', 'GodMode dev', 'site', 'Idle', '2026-09-28T06:00:00Z'),
  ]);

  it.each<SidebarGroupBy>(['root', 'recent', 'status'])('by %s is one profile group', groupBy => {
    const groups = rebuildHierarchy([serverA(), serverB()], 'All', groupBy).profileGroups;
    expect(groups.map(g => `${g.name} (${g.projectCount})`)).toEqual(['GodMode dev (5)', 'VoiceBot dev (1)']);
  });

  it('by root has a header per server, qualified by server where two share a root name', () => {
    expect(outline([serverA(), serverB()], 'root').slice(0, 11)).toEqual([
      'GodMode dev (5)',
      '  GodMode (Server A) +',
      '    A:feat-296',
      '    A:bug-300',
      '  GodMode (Server B) +',
      '    B:feat-296',
      '  site +',
      '    B:docs',
      '  tools',
      '    A:lint',
      'VoiceBot dev (1)',
    ]);
  });

  it('labels a project name shown from both with its server', () => {
    const items = rebuildHierarchy([serverA(), serverB()], 'All', 'recent').profileGroups[0].rootGroups[0].items;
    expect(items.filter(i => i.project.Name === 'feat-296').map(i => i.serverLabel)).toEqual(['Server B', 'Server A']);
  });
});

// ── Nesting (#390): a session sits under the session that started it, on several levels ──

const childOf = (parent: string, id: string, profile: string, root: string, state: ProjectState = 'Idle', updatedAt = '2026-09-28T09:00:00Z'): ProjectSummary =>
  ({ ...projectOf(id, profile, root, state, updatedAt), ParentId: parent });

/** The list as text, each child two spaces in from its parent, with its notes: `[root]` its own root, `<- name` started by. */
function tree(connections: ServerConnection[], groupBy: SidebarGroupBy = 'root', filter = 'All', otherRoot?: OtherRootChildren): string[] {
  const lines = (i: SidebarItem, depth: number): string[] => [
    `${'  '.repeat(depth + 2)}${i.project.Name}${i.ownRoot ? ` [${i.ownRoot}]` : ''}${i.startedBy ? ` <- ${i.startedBy}` : ''}`,
    ...i.children.flatMap(c => lines(c, depth + 1)),
  ];
  return rebuildHierarchy(connections, filter, groupBy, otherRoot).profileGroups.flatMap(g => [
    `${g.name} (${g.projectCount})`,
    ...g.rootGroups.flatMap(rg => [...(rg.flat ? [] : [`  ${rg.name}`]), ...rg.items.flatMap(i => lines(i, 0))]),
  ]);
}

/**
 * Profile P: an overseer in fleet, its epic overseer, and the epic's workers, one in fleet and one in work.
 * An orphan in work whose parent is gone. Profile Q: a session the overseer started in another profile.
 */
const fleet = () => server('A', [rootOf('fleet', 'P'), rootOf('work', 'P'), rootOf('other', 'Q')], [
  projectOf('overseer', 'P', 'fleet', 'Running', '2026-09-28T08:00:00Z'),
  childOf('P/fleet/epic', 'worker-1', 'P', 'work'),
  childOf('P/fleet/overseer', 'epic', 'P', 'fleet'),
  childOf('P/fleet/epic', 'worker-2', 'P', 'fleet'),
  childOf('P/work/gone', 'orphan', 'P', 'work'),
  childOf('P/fleet/overseer', 'elsewhere', 'Q', 'other'),
]);

describe('nesting', () => {
  it('nests three levels, a child in another root under its parent with its own root marked', () => {
    expect(tree([fleet()])).toEqual([
      'P (5)',
      '  fleet',
      '    overseer',
      '      epic',
      '        worker-2',
      '        worker-1 [work]',
      '  work',
      '    orphan',
      'Q (1)',
      '  other',
      '    elsewhere <- overseer',
    ]);
  });

  it('keeps a child in another root in its own root, noting its parent, when asked to', () => {
    expect(tree([fleet()], 'root', 'All', 'stay').slice(0, 9)).toEqual([
      'P (5)',
      '  fleet',
      '    overseer',
      '      epic',
      '        worker-2',
      '  work',
      '    worker-1 <- epic',
      '    orphan',
      'Q (1)',
    ]);
  });

  it('gives a session whose parent is gone the top level, with no note', () => {
    expect(tree([fleet()])).toContain('    orphan');
  });

  it('never mixes profiles: a parent in another profile leaves the child at the top of its own, noted', () => {
    expect(tree([fleet()], 'root', 'Q')).toEqual(['Q (1)', '  other', '    elsewhere <- overseer']);
  });

  it.each<SidebarGroupBy>(['recent', 'status'])('nests by %s too, under the profile', groupBy => {
    const shown = tree([fleet()], groupBy);
    // No root headers, so no root markers: each row's meta names its root
    expect(shown.slice(shown.indexOf('    overseer'), shown.indexOf('    overseer') + 4)).toEqual(['    overseer', '      epic', '        worker-2', '        worker-1']);
    expect(shown.filter(l => l.trim() === 'epic')).toEqual(['      epic']);
  });

  it('nests only under a parent on its own server', () => {
    const b = server('B', [rootOf('fleet', 'P')], [childOf('P/fleet/overseer', 'stray', 'P', 'fleet')]);
    expect(tree([fleet(), b]).filter(l => l.includes('stray'))).toEqual(['    stray']);
  });

  it("counts the root's own sessions for the root, wherever they are shown", () => {
    const groups = rebuildHierarchy([fleet()], 'All', 'root').profileGroups[0].rootGroups;
    expect(groups.map(g => `${g.rootName}: ${g.sessionCount}`)).toEqual(['fleet: 3', 'work: 2']);
  });
});

describe('a cycle planted in status.json', () => {
  /** c1 and c2 name each other, c3 itself; c4's parent is c1. */
  const cyclic = () => server('A', [rootOf('work', 'P')], [
    childOf('P/work/c2', 'c1', 'P', 'work'),
    childOf('P/work/c1', 'c2', 'P', 'work'),
    childOf('P/work/c3', 'c3', 'P', 'work'),
    childOf('P/work/c1', 'c4', 'P', 'work'),
  ]);

  it.each<OtherRootChildren>(['nest', 'stay'])('ends, its sessions at the top level, and what hangs off it under them (%s)', otherRoot => {
    expect(tree([cyclic()], 'root', 'All', otherRoot)).toEqual(['P (4)', '  work', '    c1', '      c4', '    c2', '    c3']);
  });
});
