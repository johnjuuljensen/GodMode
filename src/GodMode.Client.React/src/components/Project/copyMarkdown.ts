import type { ClipboardEvent } from 'react';

/** Puts a reply on the clipboard: its markdown as text/plain, its rendered HTML as text/html. Whether it got there. */
export async function copyMarkdown(markdown: string, html: string): Promise<boolean> {
  try {
    await navigator.clipboard.write([new ClipboardItem({
      'text/plain': new Blob([markdown], { type: 'text/plain' }),
      'text/html': new Blob([html], { type: 'text/html' }),
    })]);
    return true;
  } catch {
    // No ClipboardItem, or no text/html allowed: the markdown alone
    try {
      await navigator.clipboard.writeText(markdown);
      return true;
    } catch {
      return false;
    }
  }
}

const words = (text: string) => text.replace(/\s+/g, ' ').trim();

/**
 * A copy of a whole rendered message (a selection whose text is all of it, and no more) puts its
 * markdown in text/plain, so a list keeps its bullets in a plain-text target. Any other selection is
 * copied as the WebView does it: a part of a message does not map back to its markdown reliably.
 */
export function copyWholeAsMarkdown(e: ClipboardEvent<HTMLElement>, markdown: string) {
  const selected = window.getSelection()?.toString() ?? '';
  const el = e.currentTarget;
  if (selected.trim() === '' || words(selected) !== words(el.textContent ?? '')) return;
  e.preventDefault();
  e.clipboardData.setData('text/plain', markdown);
  e.clipboardData.setData('text/html', el.innerHTML);
}
