import { readFileSync } from 'node:fs';
import type { PendingQuestion } from '../signalr/types';

/** What cuts text off rather than wrapping or scrolling it: an ellipsis, a line clamp, no wrapping, hidden overflow. */
const CUTS = /text-overflow\s*:\s*ellipsis|line-clamp|white-space\s*:\s*nowrap|overflow(-[xy])?\s*:\s*(hidden|clip)/;

/** Every class on `root` and the elements in it. */
export const classesIn = (root: Element) =>
  new Set([root, ...root.querySelectorAll('*')].flatMap(el => [...el.classList]));

/**
 * The rules of the CSS file at `path` that name one of `classes` and cut text off (#454). jsdom applies no
 * style sheet, so a test reads the rules a rendered element's classes would get.
 */
export function cuttingRules(path: string, classes: ReadonlySet<string>): string[] {
  const css = readFileSync(path, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  return [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)]
    .filter(([, selector, body]) => CUTS.test(body)
      && [...selector.matchAll(/\.([\w-]+)/g)].some(([, name]) => classes.has(name)))
    .map(([rule]) => rule.trim().replace(/\s+/g, ' '));
}

/** The declarations of the first rule in the CSS file at `path` whose selector is exactly `selector`. */
export function ruleOf(path: string, selector: string): string {
  const css = readFileSync(path, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  return [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].find(([, s]) => s.trim() === selector)?.[2] ?? '';
}

const long = (what: string) =>
  `${what}: ${Array.from({ length: 30 }, (_, i) => `word${i}`).join(' ')} and the end of ${what.toLowerCase()}`;

/** An AskUserQuestion of three long questions with long descriptions, the second a multi-select (#454). */
export const longRequest: PendingQuestion = {
  RequestId: 'long',
  RequestedAt: '2026-10-03T12:00:00Z',
  Questions: [
    { Question: long('Repair the folder'), Header: 'Repair', MultiSelect: false, Options: [
      { Label: 'Yes, repair now', Description: long('Deletes the stale worktree folder') },
      { Label: 'No, leave it', Description: long('Keeps the folder') },
    ] },
    { Question: long('Which issues to file'), Header: 'File issues', MultiSelect: true, Options: [
      { Label: 'The crash', Description: long('Files the crash') },
      { Label: 'The typo', Description: long('Files the typo') },
      { Label: 'The leak', Description: null },
    ] },
    { Question: long('Start the fixes'), Header: 'Fixes', MultiSelect: false, Options: [
      { Label: 'Start them', Description: long('Starts a session per issue') },
      { Label: 'Wait', Description: long('Starts nothing') },
    ] },
  ],
};

/** Every piece of text in `request` a reader must be able to read. */
export const textsOf = (request: PendingQuestion) => request.Questions.flatMap(q =>
  [q.Question, q.Header ?? '', ...q.Options.flatMap(o => [o.Label, o.Description ?? ''])]).filter(Boolean);
