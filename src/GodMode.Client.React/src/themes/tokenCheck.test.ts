/// <reference types="node" />
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { describe, expect, it } from 'vitest';
import { checkTokens, formatViolation } from './tokenCheck';

// Every stylesheet in the client, as text, read from disk (Vitest blanks a CSS import, ?raw too).
// The themes' own are where the literals live
const root = join(import.meta.dirname, '..', '..');
const stylesheets = Object.fromEntries(
  readdirSync(join(root, 'src'), { recursive: true, encoding: 'utf8' })
    .filter(f => f.endsWith('.css'))
    .map(f => [relative(root, join(root, 'src', f)).split(sep).join('/'), readFileSync(join(root, 'src', f), 'utf8')]));
const isTheme = (path: string) => path.startsWith('src/themes/');

describe('component CSS names tokens only', () => {
  it('finds the stylesheets', () => {
    expect(Object.keys(stylesheets).filter(p => !isTheme(p)).length).toBeGreaterThan(10);
    expect(Object.keys(stylesheets).filter(isTheme).length).toBeGreaterThan(0);
    expect(Object.values(stylesheets).every(css => css.length > 0)).toBe(true);
  });

  it('has no literal colour, radius, shadow, blur or font family outside src/themes/', () => {
    const violations = Object.entries(stylesheets)
      .filter(([path]) => !isTheme(path))
      .flatMap(([path, css]) => checkTokens(path, css))
      .map(formatViolation);
    expect(violations).toEqual([]);
  });
});

describe('the check', () => {
  const rules = (css: string) => checkTokens('x.css', css).map(v => v.rule);

  it.each([
    ['.a { color: #fff; }', 'colour'],
    ['.a { background: rgba(0,0,0,0.5); }', 'colour'],
    ['.a { border: 1px solid rgb(94 158 255); }', 'colour'],
    ['.a { color: hsl(10 20% 30%); }', 'colour'],
    ['.a { color: white; }', 'colour'],
    ['.a { background: linear-gradient(90deg, transparent, #333); }', 'colour'],
    ['.a { border-radius: 8px; }', 'radius'],
    ['.a { border-radius: 50%; }', 'radius'],
    ['.a { border-top-left-radius: 4px; }', 'radius'],
    ['.a { border-radius: var(--radius) 4px; }', 'radius'],
    ['.a { box-shadow: 0 0 0 3px var(--accent-soft); }', 'shadow'],
    ['.a { box-shadow: var(--shadow-card), 0 1px 2px var(--x); }', 'shadow'],
    ['.a { text-shadow: 0 0 2px var(--accent); }', 'shadow'],
    ['.a { backdrop-filter: blur(20px); }', 'blur'],
    ['.a { -webkit-backdrop-filter: blur(20px) saturate(180%); }', 'blur'],
    ['.a { filter: blur(2px); }', 'blur'],
    ['.a { font-family: monospace; }', 'font-family'],
    ['.a { font-family: "DM Sans", var(--font-body); }', 'font-family'],
    ['.a { font: 12px Arial; }', 'font-family'],
    ['@keyframes k { 50% { box-shadow: 0 0 8px var(--warning); } }', 'shadow'],
  ])('fails %s', (css, rule) => {
    expect(rules(css)).toContain(rule);
  });

  it.each([
    '.a { color: var(--text-primary); background: transparent; border-color: currentColor; }',
    '.a { background: linear-gradient(90deg, transparent, var(--accent), transparent); }',
    '.a { border-radius: var(--radius-lg) var(--radius-lg) 0 var(--radius-lg); }',
    '.a { border-radius: 0; }',
    '.a { box-shadow: var(--shadow-focus), var(--shadow-inset); }',
    '.a { box-shadow: none; }',
    '.a { backdrop-filter: var(--blur-panel); -webkit-backdrop-filter: var(--blur-panel); }',
    '.a { filter: brightness(1.08); }',
    '.a { font-family: var(--font-mono); font: inherit; }',
    '.a { color: var(--x, #fff); }',
    '.a { background-image: url("data:image/svg+xml,%23n"); }',
    '/* #fff, border-radius: 8px */ .a { color: var(--accent); }',
    '.a:hover .b, input:focus { color: var(--accent); }',
    '@media (max-width: 768px) { .a { border-radius: var(--radius); } }',
    '.a { transition: color var(--duration); }',
  ])('passes %s', css => {
    expect(rules(css)).toEqual([]);
  });

  it('names the file and the line', () => {
    expect(checkTokens('x.css', '.a {\n  color: var(--accent);\n  border-radius: 7px;\n}').map(formatViolation))
      .toEqual(['x.css:3 radius: border-radius: 7px']);
  });
});
