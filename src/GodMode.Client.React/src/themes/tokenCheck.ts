/**
 * The look lives in the themes (#296): component CSS names tokens, and never a literal colour, radius,
 * shadow, blur or font family. This scans a stylesheet for them; tokenCheck.test.ts runs it over every
 * stylesheet outside src/themes/, which is where the literals belong.
 *
 * Allowed without a token: `transparent`, `currentColor` and the CSS-wide keywords; a radius of `0`
 * (a corner that is square in every theme, where a panel joins its header); `none` for a shadow or a
 * filter. Everything else goes through `var(--…)`.
 */

export type TokenRule = 'colour' | 'radius' | 'shadow' | 'blur' | 'font-family';

export interface TokenViolation {
  file: string;
  line: number;
  rule: TokenRule;
  declaration: string;
}

const namedColours = [
  'white', 'black', 'red', 'green', 'blue', 'yellow', 'orange', 'purple', 'pink', 'gray', 'grey',
  'cyan', 'magenta', 'lime', 'navy', 'teal', 'maroon', 'olive', 'silver', 'gold', 'aqua', 'fuchsia',
  'brown', 'indigo', 'violet',
];
const colourFunction = /\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)\(/i;
const hexColour = /#[0-9a-f]{3,8}\b/i;
const namedColour = new RegExp(`(?<![\\w-])(?:${namedColours.join('|')})(?![\\w-])`, 'i');
const cssWide = /^(?:inherit|initial|unset|revert|revert-layer)$/i;

/** A value with its var(…) references taken out, so a fallback inside one is not read as the value. */
const withoutVars = (value: string): string => {
  let out = '';
  let depth = 0;
  for (let i = 0; i < value.length; i++) {
    if (depth === 0 && value.startsWith('var(', i)) { depth = 1; i += 3; out += ' '; continue; }
    if (depth > 0) {
      if (value[i] === '(') depth++;
      else if (value[i] === ')') depth--;
      continue;
    }
    out += value[i];
  }
  return out;
};

/** Splits on commas outside parentheses. */
const splitList = (value: string): string[] => {
  const parts: string[] = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < value.length; i++) {
    if (value[i] === '(') depth++;
    else if (value[i] === ')') depth--;
    else if (value[i] === ',' && depth === 0) { parts.push(value.slice(start, i)); start = i + 1; }
  }
  parts.push(value.slice(start));
  return parts.map(p => p.trim());
};

const isVar = (part: string) => /^var\(--[\w-]+\)$/.test(part);

function rulesBroken(property: string, value: string): TokenRule[] {
  const broken: TokenRule[] = [];
  const bare = withoutVars(value);
  // url(…) is an image, not a colour, even where it has a # in it
  const inspected = bare.replace(/url\([^)]*\)/gi, ' ');
  if (hexColour.test(inspected) || colourFunction.test(inspected) || (property !== 'font-family' && namedColour.test(inspected)))
    broken.push('colour');

  if (/^border(?:-[a-z]+)*-radius$/.test(property) && !cssWide.test(value)
    && !value.split(/\s+/).every(part => part === '0' || isVar(part)))
    broken.push('radius');

  if ((property === 'box-shadow' || property === 'text-shadow') && !cssWide.test(value) && value !== 'none'
    && !splitList(value).every(isVar))
    broken.push('shadow');

  if ((property === 'backdrop-filter' || property === '-webkit-backdrop-filter')
    && !cssWide.test(value) && value !== 'none' && !isVar(value))
    broken.push('blur');
  else if (property === 'filter' && /\bblur\(/i.test(bare))
    broken.push('blur');

  if (property === 'font-family' && !cssWide.test(value) && !isVar(value))
    broken.push('font-family');
  else if (property === 'font' && !cssWide.test(value) && !/var\(--/.test(value))
    broken.push('font-family');

  return broken;
}

/** Every declaration in `css` that names a literal where a token belongs. */
export function checkTokens(file: string, css: string): TokenViolation[] {
  // Comments out, with their newlines kept so lines still count
  const text = css.replace(/\/\*[\s\S]*?\*\//g, c => c.replace(/[^\n]/g, ' '));
  const violations: TokenViolation[] = [];
  // A declaration: a property, a colon, a value up to ; or } (selectors hold no ; or {)
  const declaration = /(^|[{;\s])(-?-?[a-zA-Z][\w-]*)\s*:\s*([^;{}]+?)\s*(?=[;}])/g;
  for (const match of text.matchAll(declaration)) {
    const property = match[2].toLowerCase();
    const value = match[3].replace(/\s*!important$/i, '').replace(/\s+/g, ' ');
    // A selector's pseudo-class (a:hover {) is followed by {, which the value cannot hold; skip @-rule preludes
    const start = match.index + match[1].length;
    const before = text.lastIndexOf('@', start);
    if (before >= 0 && !/[;{}]/.test(text.slice(before, start))) continue;
    for (const rule of rulesBroken(property, value)) {
      violations.push({ file, line: text.slice(0, start).split('\n').length, rule, declaration: `${property}: ${value}` });
    }
  }
  return violations;
}

export const formatViolation = (v: TokenViolation) => `${v.file}:${v.line} ${v.rule}: ${v.declaration}`;
