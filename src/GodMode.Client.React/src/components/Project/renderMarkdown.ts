import { Marked } from 'marked';
import DOMPurify from 'dompurify';

// Transcript text is untrusted (a tool result can put anything into claude's reply): markdown is
// rendered to HTML, then DOMPurify strips scripts, event handlers and javascript: URLs. Only HTML
// survives: an SVG or MathML link, or an image map's area, would slip past the link rule below
const marked = new Marked({ gfm: true, breaks: true, async: false });

const SANITIZE = {
  USE_PROFILES: { html: true },
  FORBID_TAGS: ['style', 'form', 'input', 'button', 'textarea', 'select', 'area', 'map'],
};

// An address with a scheme. A relative one (src/foo.ts, #top, //host) would resolve against the app's own page
function isAbsolute(href: string): boolean {
  try {
    new URL(href);
    return true;
  } catch {
    return false;
  }
}

let hooked = false;
function purify(html: string): string {
  if (!hooked) {
    // A link opens outside the app, and cannot reach back into it. One into the app is no link
    DOMPurify.addHook('afterSanitizeAttributes', node => {
      const href = node.getAttribute('href') ?? node.getAttribute('xlink:href');
      if (href === null) return;
      if (isAbsolute(href)) {
        node.setAttribute('target', '_blank');
        node.setAttribute('rel', 'noopener noreferrer');
      } else {
        node.removeAttribute('href');
        node.removeAttribute('xlink:href');
      }
    });
    hooked = true;
  }
  const fragment = DOMPurify.sanitize(html, { ...SANITIZE, RETURN_DOM_FRAGMENT: true });
  // A link left without an address (relative, or javascript:) shows as its text
  for (const a of fragment.querySelectorAll('a:not([href])')) a.replaceWith(...a.childNodes);
  // Serialized in DOMPurify's inert document, where an image does not start loading
  const box = fragment.ownerDocument.createElement('div');
  box.append(fragment);
  return box.innerHTML;
}

// A virtualized row mounts again each time it scrolls into view: keep what was rendered
const CACHE_SIZE = 500;
const cache = new Map<string, string>();

export function renderMarkdown(text: string): string {
  const hit = cache.get(text);
  if (hit !== undefined) {
    cache.delete(text);
    cache.set(text, hit);
    return hit;
  }
  const html = purify(marked.parse(text) as string);
  cache.set(text, html);
  if (cache.size > CACHE_SIZE) cache.delete(cache.keys().next().value!);
  return html;
}
