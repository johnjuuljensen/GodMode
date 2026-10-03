/**
 * A root is shown by its title (#434), its name staying its key: in the left list's headers (with the name as the
 * header's tooltip, and beside the title when two roots there would show one title), in a session's meta and its
 * own-root mark, and wherever a session's root is looked up by its name.
 */
import { describe, expect, it } from 'vitest';
import type { GodModeHub } from '../signalr/hub';
import type { ProjectRootInfo, ProjectSummary } from '../signalr/types';
import { rebuildHierarchy, rootShown, type ServerConnection } from './hierarchy';

const issue = [{ Name: 'issue', AllowSkipPermissions: false, Session: true, Transient: false }];

const rootOf = (name: string, profile: string, title?: string): ProjectRootInfo =>
  ({ Name: name, ProfileName: profile, Actions: issue, Title: title ?? null });

const projectOf = (id: string, profile: string, root: string, parentId?: string): ProjectSummary =>
  ({ Id: `${profile}/${root}/${id}`, Name: id, State: 'Idle', UpdatedAt: '2026-10-03T09:00:00Z', RootName: root, ProfileName: profile,
    SharedFolder: false, Adopted: false, ParentId: parentId ? `${profile}/${parentId}` : null });

const server = (id: string, roots: ProjectRootInfo[], projects: ProjectSummary[]): ServerConnection => ({
  serverInfo: { Id: id, Name: `Server ${id}` } as ServerConnection['serverInfo'],
  hub: {} as GodModeHub,
  connectionState: 'connected',
  projects,
  roots,
  profiles: [...new Set(roots.map(r => r.ProfileName!))].map(Name => ({ Name })),
});

const headers = (connections: ServerConnection[]) =>
  rebuildHierarchy(connections, 'All', 'root').profileGroups.map(g =>
    [g.name, g.rootGroups.map(rg => ({ name: rg.name, rootName: rg.rootName, tooltip: rg.tooltip }))] as const);

describe('root titles', () => {
  it("shows a root by its title, its name as the header's tooltip and the key its + creates in", () => {
    expect(headers([server('A', [rootOf('Mega-Assistant', 'Mega', 'Assistant'), rootOf('GodMode', 'Mega')], [])])).toEqual([
      ['Mega', [
        { name: 'Assistant', rootName: 'Mega-Assistant', tooltip: 'Mega-Assistant' },
        { name: 'GodMode', rootName: 'GodMode', tooltip: undefined },
      ]],
    ]);
  });

  it('shows two profiles\' roots of one title each by the title, under its profile', () => {
    const conn = server('A', [rootOf('Outbound-Assistant', 'Outbound', 'Assistant'), rootOf('Mega-Assistant', 'Mega', 'Assistant')], []);
    expect(headers([conn]).map(([profile, roots]) => [profile, roots.map(r => r.name)])).toEqual([
      ['Mega', ['Assistant']],
      ['Outbound', ['Assistant']],
    ]);
  });

  it('adds the name to a title another root in the list is shown as too', () => {
    const conn = server('A', [rootOf('Work-Notes', 'P', 'Notes'), rootOf('Notes', 'P'), rootOf('Home-Notes', 'P', 'Notes')], []);
    expect(headers([conn])[0][1].map(r => r.name)).toEqual(['Notes', 'Notes (Home-Notes)', 'Notes (Work-Notes)']);
  });

  it('marks a session nested in another root with that root\'s title, and gives each its root as shown', () => {
    const conn = server('A', [rootOf('godmode', 'P', 'GodMode'), rootOf('voicebot', 'P', 'VoiceBot')], [
      projectOf('overseer', 'P', 'godmode'),
      projectOf('worker', 'P', 'voicebot', 'godmode/overseer'),
    ]);
    const [overseer] = rebuildHierarchy([conn], 'All', 'root').profileGroups[0].rootGroups[0].items;
    expect(overseer.rootShown).toBe('GodMode');
    expect(overseer.children[0]).toMatchObject({ ownRoot: 'VoiceBot', rootShown: 'VoiceBot' });
  });

  it("looks a session's root up by its profile and name: its title, else the name", () => {
    const roots = [rootOf('Mega-Assistant', 'Mega', 'Assistant'), rootOf('Mega-Assistant', 'Other'), rootOf('plain', 'Mega')];
    expect(rootShown(roots, 'mega', 'Mega-Assistant')).toBe('Assistant');
    expect(rootShown(roots, 'Other', 'Mega-Assistant')).toBe('Mega-Assistant');
    expect(rootShown(roots, 'Mega', 'plain')).toBe('plain');
    expect(rootShown(roots, 'Mega', 'gone')).toBe('gone');
    expect(rootShown(undefined, null, 'gone')).toBe('gone');
  });
});
