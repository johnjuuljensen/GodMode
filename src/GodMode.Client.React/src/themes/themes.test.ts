/// <reference types="node" />
/**
 * The theme registry and its stylesheets (#296): each theme's tokens are scoped to it, a theme beyond Glass defines
 * every token (none falls through to Glass's look), and every token a stylesheet names exists.
 */
import { createHash } from 'node:crypto';
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
    expect(resolveThemeId('commodore')).toBe('commodore');
    expect(resolveThemeId('lcars')).toBe('lcars');
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

// A theme's fonts are its own chunk, fetched the first time it is applied; the bundle's are Glass's alone.
// Each is an @fontsource package, or a font bundled here whose @font-face is a stylesheet the module imports
it.each(themes.filter(t => !t.id.startsWith('glass-')).map(t => [t.id, t.fonts] as const))('%s loads its fonts in a module of its own', (id, fonts) => {
  const module = read(`${id}-fonts.ts`);
  const faces = [...module.matchAll(/^import '(\.\/[^']+\.css)';/gm)].map(m => read(m[1]))
    .flatMap(css => [...css.matchAll(/@font-face\s*\{[^}]*font-family:\s*'([^']+)'/g)].map(m => m[1]));
  for (const font of fonts) {
    if (!faces.includes(font)) expect(module).toContain(`@fontsource/${font.toLowerCase().replace(/ /g, '-')}/`);
  }
  expect(read('index.ts')).toContain(`import('./${id}-fonts')`);
});

// Pet Me 64 is Kreative Software's, given away only with its licence verbatim and credit, and never modified
describe("Commodore's bundled font", () => {
  const petMe = (file: string) => readFileSync(join(dir, 'fonts', 'pet-me', file));
  const sha256 = (file: string) => createHash('sha256').update(petMe(file)).digest('hex');

  it('is the font and the licence Kreative ships, byte for byte', () => {
    expect(sha256('PetMe64.ttf')).toBe('1a5a4bf4af2076345480b1a99490a16503f0f5c167e200c8e7644fd40b72d9fd');
    expect(sha256('FreeLicense.txt')).toBe('5b26f7318dddc8d1ace2353992924d5dc0fc6123656de7bdd81ac69f9a8b0878');
  });

  it('credits Kreative Software', () => {
    expect(petMe('README.md').toString('utf8')).toContain('Kreative Software');
  });
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

  // #303: under a dark color-scheme a .btn with no background of its own gets the browser's grey face; a theme gives it
  // its surface at zero specificity, so .btn-primary and the rest keep theirs
  it.each(themes.filter(t => !t.id.startsWith('glass-')).map(t => t.id))('%s gives a plain .btn a background', id => {
    expect(read(`${id}.css`).replace(/\/\*[\s\S]*?\*\//g, ''))
      .toMatch(new RegExp(`:where\\(:root\\[data-theme="${id}"\\] \\.btn\\)\\s*\\{[^}]*background:`));
  });

  // #316: a native select's open list lays a translucent option background on white, under the page's light text.
  // So a theme's --option-bg is opaque: a hex colour, or its --glass where that is one
  it.each(themes.map(t => t.id))('%s gives a native list\'s options an opaque background', id => {
    const token = (name: string) => read(`${id}.css`).match(new RegExp(`${name}:\\s*([^;]+);`))?.[1].trim();
    const value = token('--option-bg') === 'var(--glass)' ? token('--glass') : token('--option-bg');
    expect(value).toMatch(/^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i);
  });

  // #437: a theme restyles the left list's spacing, never widens it much; the panel's width (#436) counts on it.
  // Glass light keeps Glass dark's spacing
  const listBounds = { '--list-indent': [8, 12], '--list-gutter': [0, 8], '--row-pad-x': [2, 8], '--row-pad-y': [1, 6], '--state-dot-size': [5, 10] };
  it.each(themes.map(t => t.id))('%s keeps the left list compact', id => {
    const px = (name: string) => {
      const value = (read(`${id}.css`).match(new RegExp(`${name}:\\s*([^;]+);`)) ?? read('glass-dark.css').match(new RegExp(`${name}:\\s*([^;]+);`)))?.[1].trim();
      return Number(value?.match(/^(\d+(?:\.\d+)?)px$/)?.[1] ?? NaN);
    };
    for (const [name, [min, max]] of Object.entries(listBounds)) {
      expect({ name, px: px(name) }).toEqual({ name, px: expect.toSatisfy((v: number) => v >= min && v <= max) });
    }
  });

  it('every token a stylesheet names is defined', () => {
    const named = new Set(allCss.flatMap(css => [...css.matchAll(/var\((--[\w-]+)/g)].map(m => m[1])));
    expect([...named].filter(t => !glassTokens.has(t))).toEqual([]);
  });
});
