// @vitest-environment jsdom
/**
 * Markdown from the transcript: every link it keeps opens outside the app (MAUI's WebView shows only the
 * app), and nothing survives that could hold a link the rule does not see.
 */
import { describe, expect, it } from 'vitest';
import { renderMarkdown } from './renderMarkdown';

const HTML_NS = 'http://www.w3.org/1999/xhtml';

function rendered(text: string): HTMLElement {
  const el = document.createElement('div');
  el.innerHTML = renderMarkdown(text);
  return el;
}

const links = (el: HTMLElement) =>
  [...el.querySelectorAll('*')].filter(n => n.hasAttribute('href') || n.hasAttribute('xlink:href'));

// Each kind of link an HTML sanitiser's defaults keep, and a javascript: one
const EVERY_KIND = [
  '[markdown](https://example.invalid/md)',
  '<a href="https://example.invalid/html">html</a>',
  '<svg><a href="https://example.invalid/svg"><text>svg</text></a><a xlink:href="https://example.invalid/xlink"><text>xlink</text></a></svg>',
  '<map name="m"><area href="https://example.invalid/area" shape="rect" coords="0,0,9,9"></map><img usemap="#m" src="data:image/png;base64,iVBORw0KGgo=">',
  '<math><mi href="https://example.invalid/math">x</mi></math>',
  '[script](javascript:alert(1))',
].join('\n\n');

describe('a link in markdown', () => {
  it('opens outside the app, whatever kind it is', () => {
    const found = links(rendered(EVERY_KIND));
    expect(found.length).toBeGreaterThan(0);
    for (const link of found) {
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    }
  });

  it('leaves no SVG, MathML, area or map', () => {
    const el = rendered(EVERY_KIND);
    expect(el.querySelector('svg, math, area, map')).toBeNull();
    expect([...el.querySelectorAll('*')].filter(n => n.namespaceURI !== HTML_NS)).toEqual([]);
    expect(links(el).map(a => a.getAttribute('href'))).toEqual(['https://example.invalid/md', 'https://example.invalid/html']);
  });

  it('is no link when it is javascript:', () => {
    const el = rendered('[run it](javascript:alert(2)) and <a href="javascript:alert(3)">this</a>');
    expect(links(el)).toEqual([]);
    expect(el.querySelector('a')).toBeNull();
    expect(el.textContent?.trim()).toBe('run it and this');
  });

  it('shows a relative one as its text, not as a link into the app', () => {
    const el = rendered('See [the file](src/foo.ts), [the top](#top) and <a href="//example.invalid/x">that host</a>.');
    expect(el.querySelector('a')).toBeNull();
    expect(el.textContent?.trim()).toBe('See the file, the top and that host.');
  });

  it('keeps a mailto: link, which the app itself drops (it opens only http and https)', () => {
    const [mail] = links(rendered('[mail](mailto:someone@example.invalid)'));
    expect(mail.getAttribute('href')).toBe('mailto:someone@example.invalid');
    expect(mail.getAttribute('target')).toBe('_blank');
  });
});
