/**
 * A fold's onMouseDown: a pointer press leaves the focus where it was, so a pending question's keys, which
 * are the prompt's or the page's (#240), still reach it. Chromium on Windows and WebView2 would focus the
 * fold and keep it there. A key press is as before (#218).
 */
export const keepFocus = (e: { preventDefault: () => void }) => e.preventDefault();
