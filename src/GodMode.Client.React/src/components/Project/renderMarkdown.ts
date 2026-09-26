import { Marked } from 'marked';
import DOMPurify, { type DOMPurify as Purifier } from 'dompurify';

// Transcript text is untrusted (a tool result can put anything into claude's reply): markdown is
// rendered to HTML, then DOMPurify strips scripts, event handlers and javascript: URLs. Only HTML
// survives: an SVG or MathML link, or an image map's area, would slip past the link rule below
const marked = new Marked({ gfm: true, breaks: true, async: false });

// Whatever loads as it renders would tell a third party who read the transcript, and when: media go,
// as do the attributes that fetch past an image's src, and an image becomes a link (see images below)
const MEDIA_TAGS = ['picture', 'source', 'video', 'audio', 'track'];
const MEDIA_ATTRS = ['srcset', 'background', 'poster'];
// An element that styles itself, or takes one of the app's classes (.modal-overlay is fixed and full
// screen), could cover the page, the permission card included; the transcript's CSS then decides nothing
const STYLE_ATTRS = ['style', 'class', 'id'];

const SANITIZE = {
  USE_PROFILES: { html: true },
  FORBID_TAGS: ['style', 'form', 'input', 'button', 'textarea', 'select', 'area', 'map', ...MEDIA_TAGS],
  FORBID_ATTR: [...STYLE_ATTRS, ...MEDIA_ATTRS],
};

// An address with a scheme, or null. A relative one (src/foo.ts, #top, //host) would resolve against the app's own page
function absolute(href: string): URL | null {
  try {
    return new URL(href);
  } catch {
    return null;
  }
}

// A link opens outside the app, and cannot reach back into it
function opensOutside(link: Element) {
  link.setAttribute('target', '_blank');
  link.setAttribute('rel', 'noopener noreferrer');
}

// The app's own instance: a hook on DOMPurify's shared one would reach every other caller of it
let purifier: Purifier | undefined;
function instance(): Purifier {
  if (!purifier) {
    purifier = DOMPurify(window);
    // A link into the app is no link
    purifier.addHook('afterSanitizeAttributes', node => {
      const href = node.getAttribute('href') ?? node.getAttribute('xlink:href');
      if (href === null) return;
      if (absolute(href)) {
        opensOutside(node);
      } else {
        node.removeAttribute('href');
        node.removeAttribute('xlink:href');
      }
    });
  }
  return purifier;
}

/**
 * An image loads nothing: one on the web becomes a link to it, which opens outside the app only when
 * clicked, and any other (relative, so from the app's own server) its text. A data: image, which is
 * in the transcript already, stays. Inside a link an image is its text, as a link holds no link.
 */
function images(fragment: DocumentFragment) {
  for (const img of fragment.querySelectorAll('img')) {
    const src = img.getAttribute('src') ?? '';
    if (/^data:image\//i.test(src)) continue;
    const alt = img.getAttribute('alt')?.trim();
    const text = alt ? `Image: ${alt}` : 'Image';
    if (/^https?:$/.test(absolute(src)?.protocol ?? '') && !img.closest('a')) {
      const link = img.ownerDocument.createElement('a');
      link.setAttribute('href', src);
      opensOutside(link);
      link.textContent = text;
      img.replaceWith(link);
    } else {
      img.replaceWith(text);
    }
  }
}

function purify(html: string): string {
  const fragment = instance().sanitize(html, { ...SANITIZE, RETURN_DOM_FRAGMENT: true });
  // A link left without an address (relative, or javascript:) shows as its text
  for (const a of fragment.querySelectorAll('a:not([href])')) a.replaceWith(...a.childNodes);
  images(fragment);
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
