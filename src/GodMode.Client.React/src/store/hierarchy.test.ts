/**
 * The left list is always grouped by profile, and the group-by chooses what is under each (#308): root
 * headers with their + for Root, the profile's projects directly for Recent and Status.
 */
import { describe, expect, it } from 'vitest';
import type { ConnectionState, GodModeHub } from '../signalr/hub';
import type { ProjectRootInfo, ProjectState, ProjectSummary } from '../signalr/types';
import { rebuildHierarchy, type ServerConnection, type SidebarGroupBy } from './hierarchy';

const issue = [{ Name: 'issue', AllowSkipPermissions: false, Session: true, Transient: false }];

const rootOf = (name: string, profile: string, actions = issue): ProjectRootInfo => ({ Name: name, ProfileName: profile, Actions: actions });

const projectOf = (id: string, profile: string, root: string, state: ProjectState, updatedAt: string): ProjectSummary =>
  ({ Id: `${profile}/${root}/${id}`, Name: id, State: state, UpdatedAt: updatedAt, RootName: root, ProfileName: profile, SharedFolder: false });

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
