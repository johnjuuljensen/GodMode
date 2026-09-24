import { Marked } from 'marked';
import DOMPurify from 'dompurify';

// Transcript text is untrusted (a tool result can put anything into claude's reply): markdown is
// rendered to HTML, then DOMPurify strips scripts, event handlers and javascript: URLs
const marked = new Marked({ gfm: true, breaks: true, async: false });

let hooked = false;
function purify(html: string): string {
  if (!hooked) {
    // A link opens outside the app, and cannot reach back into it
    DOMPurify.addHook('afterSanitizeAttributes', node => {
      if (node.tagName === 'A' && node.hasAttribute('href')) {
        node.setAttribute('target', '_blank');
        node.setAttribute('rel', 'noopener noreferrer');
      }
    });
    hooked = true;
  }
  return DOMPurify.sanitize(html, { FORBID_TAGS: ['style', 'form', 'input', 'button', 'textarea', 'select'] });
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
