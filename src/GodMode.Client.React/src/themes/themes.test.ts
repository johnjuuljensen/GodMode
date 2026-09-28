/// <reference types="node" />
/**
 * The theme registry and its stylesheets (#296): each theme's tokens are scoped to it, a theme beyond Glass defines
 * every token (none falls through to Glass's look), and every token a stylesheet names exists.
 */
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { defaultThemeId, resolveThemeId, themes } from '.';

const dir = import.meta.dirname;
const read = (file: string) => readFileSync(join(dir, file), 'utf8');
const src = join(dir, '..');
const allCss = readdirSync(src, { recursive: true, encoding: 'utf8' }).filter(f => f.endsWith('.css'))
  .map(f => readFileSync(join(src, f), 'utf8').replace(/\/\*[\s\S]*?\*\//g, ''));

/** The custom properties a block of CSS defines. */
const defined = (css: string) => new Set([...css.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/(--[\w-]+)\s*:/g)].map(m => m[1]));
const glassTokens = defined(read('glass-dark.css'));

describe('the registry', () => {
  it('has unique ids, Glass dark first and the default', () => {
    expect(new Set(themes.map(t => t.id)).size).toBe(themes.length);
    expect(themes[0].id).toBe(defaultThemeId);
    expect(defaultThemeId).toBe('glass-dark');
  });

  it('maps the dark/light toggle\'s values, and anything unknown to Glass dark', () => {
    expect(resolveThemeId('dark')).toBe('glass-dark');
    expect(resolveThemeId('light')).toBe('glass-light');
    expect(resolveThemeId('phosphor')).toBe('phosphor');
    expect(resolveThemeId('neon')).toBe('neon');
    expect(resolveThemeId('doom')).toBe('doom');
    expect(resolveThemeId('nope')).toBe('glass-dark');
    expect(resolveThemeId(null)).toBe('glass-dark');
  });

  it.each(themes.filter(t => t.id !== defaultThemeId).map(t => t.id))('%s has a stylesheet scoped to it', id => {
    expect(read(`${id}.css`)).toContain(`:root[data-theme="${id}"]`);
  });

  // Glass light's fonts are Glass dark's, which it leaves as they are
  it.each(themes.map(t => [t.id, t.fonts] as const))('%s lists the fonts its tokens name', (id, fonts) => {
    const css = read(`${id}.css`) + (id.startsWith('glass-') ? read('glass-dark.css') : '');
    for (const font of fonts) expect(css).toContain(`'${font}'`);
  });
});

// A theme's fonts are its own chunk, fetched the first time it is applied; the bundle's are Glass's alone
it.each(themes.filter(t => !t.id.startsWith('glass-')).map(t => [t.id, t.fonts] as const))('%s loads its fonts in a module of its own', (id, fonts) => {
  const module = read(`${id}.fonts.ts`);
  for (const font of fonts) expect(module).toContain(`@fontsource/${font.toLowerCase().replace(/ /g, '-')}/`);
  expect(read('index.ts')).toContain(`import('./${id}.fonts')`);
});

describe('the tokens', () => {
  it('Glass dark defines the look, as :root', () => {
    expect(glassTokens.size).toBeGreaterThan(100);
    expect(read('glass-dark.css')).toMatch(/^:root \{/m);
  });

  // Glass light is Glass dark in light colours; any other theme is a look of its own, all of it
  it.each(themes.filter(t => !t.id.startsWith('glass-')).map(t => t.id))('%s defines every token Glass defines', id => {
    const own = defined(read(`${id}.css`));
    expect([...glassTokens].filter(t => !own.has(t))).toEqual([]);
  });

  // Epic #295: a new theme adds no looping animation; Glass keeps its pulses
  it.each(themes.filter(t => !t.id.startsWith('glass-')).map(t => t.id))('%s loops nothing', id => {
    expect(read(`${id}.css`)).toMatch(/--loop-iterations:\s*0;/);
    expect(read(`${id}.css`)).not.toMatch(/@keyframes|animation(?:-iteration-count)?\s*:/);
  });

  it('every token a stylesheet names is defined', () => {
    const named = new Set(allCss.flatMap(css => [...css.matchAll(/var\((--[\w-]+)/g)].map(m => m[1])));
    expect([...named].filter(t => !glassTokens.has(t))).toEqual([]);
  });
});
