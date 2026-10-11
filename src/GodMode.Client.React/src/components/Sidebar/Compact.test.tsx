// @vitest-environment jsdom
/// <reference types="node" />
/**
 * A compact left list (#437): a session's state is a dot in its colour, named in its tooltip and aria-label, and
 * the name gets the row, in one line with the full name in its tooltip. The list is tight on a desktop, but a
 * phone keeps its touch targets: the rows, the fold toggles and the swipe's Delete.
 */
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import type { ProjectState } from '../../signalr/types';
import { project } from '../../test/fakeHub';
import { render, type Rendered } from '../../test/render';
import { useAppStore, projectKey } from '../../store';
import { ProjectItem, SWIPE_REVEAL_PX } from './ProjectItem';

const initialState = useAppStore.getState();
let view: Rendered | undefined;

afterEach(() => {
  view?.unmount();
  view = undefined;
  useAppStore.setState(initialState, true);
});

const row = (state: ProjectState, name = 'a session', currentQuestion?: string) => {
  const p = { ...project('p1', name, state, new Date().toISOString()), CurrentQuestion: currentQuestion };
  return render(<ProjectItem item={{ key: projectKey('A', 'p1'), serverId: 'A', project: p, children: [] }} isSelected={false} onSelect={() => {}} />);
};
const dot = () => view!.container.querySelector<HTMLElement>('.project-item .project-state-dot')!;

describe("a session's state", () => {
  it.each([
    ['Idle', 'Idle'],
    ['Running', 'Running'],
    ['WaitingInput', 'Waiting on you'],
    ['WaitingPermission', 'Waiting for your permission'],
    ['Error', 'Error'],
    ['Stopped', 'Stopped'],
  ] as const)('%s is a dot, named %s', async (state, name) => {
    view = await row(state);
    expect(dot().getAttribute('role')).toBe('img');
    expect(dot().getAttribute('aria-label')).toBe(name);
    expect(dot().title).toBe(name);
    expect([...dot().classList]).toEqual(['project-state-dot', state]);
    // A dot, no lettered badge
    expect(dot().textContent).toBe('');
    expect(view.container.querySelector('.project-state-badge')).toBeNull();
  });

  it('is waiting on you while it was stopped on a question it still asks, as its server says (#441)', async () => {
    view = await row('Stopped', 'a session', 'Shall I go on?');
    expect(dot().getAttribute('aria-label')).toBe('Waiting on you');
    expect(dot().classList.contains('WaitingInput')).toBe(true);
  });

  it('is named as the server names it when this client does not know it', async () => {
    view = await row('Hibernating' as ProjectState);
    expect(dot().getAttribute('aria-label')).toBe('Hibernating');
  });
});

it("gives the name its full text in a tooltip, as the row cuts it", async () => {
  const name = 'a name long enough to be cut where the list is narrow';
  view = await row('Idle', name);
  expect(view.container.querySelector<HTMLElement>('.project-name')!.title).toBe(name);
});

// jsdom lays nothing out, so this reads the stylesheet: what a phone's media rule gives the list's controls
describe('on a phone', () => {
  const css = readFileSync(join(import.meta.dirname, 'Sidebar.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  /** The declarations the phone's media rules give `selector`, as `property: value`. */
  const onPhone = (selector: string) => [...css.matchAll(/@media \(max-width: 768px\) \{([\s\S]*?)\n\}/g)]
    .flatMap(m => [...m[1].matchAll(/([^{}]+)\{([^}]*)\}/g)])
    .filter(rule => rule[1].split(',').map(s => s.trim()).includes(selector))
    .flatMap(rule => rule[2].split(';').map(d => d.trim()).filter(Boolean));

  it.each(['.project-item', '.project-children-toggle', '.group-fold-toggle'])('%s keeps a 44px touch target', selector => {
    expect(onPhone(selector)).toContain('min-height: 44px');
  });

  // A parent's chevron (#506) hangs left of its row, out of its flow: a parent's dot and name stay in the column of a
  // row with nothing under it, on a phone too, so the nesting never reads backwards
  it("a parent's toggle hangs in the gutter, out of the row's flow, on a phone too", () => {
    const outside = css.replace(/@media \(max-width: 768px\) \{[\s\S]*?\n\}/g, '');
    const rule = (selector: string) => [...outside.matchAll(/([^{}]+)\{([^}]*)\}/g)]
      .filter(r => r[1].split(',').map(s => s.trim()).includes(selector))
      .flatMap(r => r[2].split(';').map(d => d.trim()).filter(Boolean));
    expect(rule('.project-children-toggle')).toEqual(expect.arrayContaining([
      'position: absolute', 'left: calc(-1 * var(--list-gutter))', 'margin: 0',
    ]));
    expect(rule('.project-item-wrapper .project-item')).toContain('position: relative');
    // The phone widens it over the state dot, its chevron still in the gutter, and never puts it back in the flow
    const phone = onPhone('.project-children-toggle');
    expect(phone).toEqual(expect.arrayContaining(['justify-content: flex-start']));
    expect(phone.filter(d => /^(position|margin|min-width|flex)/.test(d))).toEqual([]);
  });

  it("the swipe still opens a full Delete", () => {
    expect(SWIPE_REVEAL_PX).toBe(88);
  });
});
