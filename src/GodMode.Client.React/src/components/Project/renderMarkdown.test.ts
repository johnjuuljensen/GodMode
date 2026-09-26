// @vitest-environment jsdom
/**
 * Markdown from the transcript: every link it keeps opens outside the app (MAUI's WebView shows only the
 * app), and nothing survives that could hold a link the rule does not see, fetch as it renders, or
 * style itself over the page.
 */
import { describe, expect, it } from 'vitest';
import DOMPurify from 'dompurify';
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

const DOT = 'data:image/png;base64,iVBORw0KGgo=';

describe('an image in markdown', () => {
  it('on the web is a link to it, which opens outside the app', () => {
    const el = rendered('![the chart](https://example.invalid/chart.png) and <img src="https://example.invalid/raw.png" alt="raw">');
    expect(el.querySelector('img')).toBeNull();
    expect(links(el).map(a => [a.getAttribute('href'), a.textContent, a.getAttribute('target'), a.getAttribute('rel')])).toEqual([
      ['https://example.invalid/chart.png', 'Image: the chart', '_blank', 'noopener noreferrer'],
      ['https://example.invalid/raw.png', 'Image: raw', '_blank', 'noopener noreferrer'],
    ]);
  });

  it('is its text when relative, as it would load from the app\'s own server', () => {
    const el = rendered('![shot](docs/shot.png) <img src="/favicon.ico"> <img alt="none">');
    expect(el.querySelector('img, a')).toBeNull();
    expect(el.textContent?.trim()).toBe('Image: shot Image Image: none');
  });

  it('is its text inside a link, which keeps its own address', () => {
    const el = rendered('[![CI](https://example.invalid/badge.svg)](https://example.invalid/actions)');
    expect(el.querySelector('img')).toBeNull();
    expect(links(el).map(a => [a.getAttribute('href'), a.textContent])).toEqual([['https://example.invalid/actions', 'Image: CI']]);
  });

  it('stays when it is data:, which is in the transcript already', () => {
    const img = rendered(`![a dot](${DOT})`).querySelector('img');
    expect(img?.getAttribute('src')).toBe(DOT);
    expect(img?.getAttribute('alt')).toBe('a dot');
  });
});

it('fetches nothing as it renders: a web address is left only in a link', () => {
  const el = rendered([
    '![md](https://example.invalid/md.png)',
    '<img src="https://example.invalid/img.png">',
    `<img src="${DOT}" srcset="https://example.invalid/srcset.png 2x">`,
    '<picture><source srcset="https://example.invalid/source.webp"><img src="https://example.invalid/fallback.png"></picture>',
    '<video src="https://example.invalid/v.mp4" poster="https://example.invalid/poster.png"><source src="https://example.invalid/s.mp4"><track src="https://example.invalid/t.vtt"></video>',
    '<audio src="https://example.invalid/a.mp3"></audio>',
    '<table background="https://example.invalid/bg.png"><tr><td background="https://example.invalid/td.png">cell</td></tr></table>',
    '<div style="background-image:url(https://example.invalid/css.png)">css</div>',
    '<iframe src="https://example.invalid/frame"></iframe><object data="https://example.invalid/obj"></object><embed src="https://example.invalid/embed">',
    '<link rel="stylesheet" href="https://example.invalid/sheet.css"><input type="image" src="https://example.invalid/input.png">',
  ].join('\n\n'));
  const fetching = [...el.querySelectorAll('*')].flatMap(node => [...node.attributes]
    .filter(attr => attr.value.includes('example.invalid') && !(node.localName === 'a' && attr.name === 'href'))
    .map(attr => `${node.localName} ${attr.name}`));
  expect(fetching).toEqual([]);
  expect([...el.querySelectorAll('img')].map(img => img.getAttribute('src'))).toEqual([DOT]);
  expect(el.querySelector('picture, source, video, audio, track')).toBeNull();
});

it('keeps no style, class or id: nothing can cover the page, whatever the app\'s CSS says', () => {
  const el = rendered([
    '<div style="position:fixed;inset:0;z-index:9999">cover</div>',
    '<div class="modal-overlay">overlay</div>',
    '<p id="root">root</p>',
    '```js\nx()\n```',
  ].join('\n\n'));
  expect([...el.querySelectorAll('[style], [class], [id]')].map(n => n.outerHTML)).toEqual([]);
  expect(el.textContent).toMatch(/cover[\s\S]*overlay[\s\S]*root[\s\S]*x\(\)/);
});

it('leaves DOMPurify\'s shared instance as it was: its links get no target', () => {
  expect(rendered('[a](https://example.invalid/own)').querySelector('a')?.getAttribute('target')).toBe('_blank');
  const box = document.createElement('div');
  box.innerHTML = DOMPurify.sanitize('<a href="https://example.invalid/shared">shared</a> <a href="src/foo.ts">relative</a>');
  expect([...box.querySelectorAll('a')].map(a => [a.getAttribute('href'), a.getAttribute('target'), a.getAttribute('rel')])).toEqual([
    ['https://example.invalid/shared', null, null],
    ['src/foo.ts', null, null],
  ]);
});
