/** Where a scroller stands: what a scroll event can tell */
export interface ScrollMetrics {
  scrollTop: number;
  scrollHeight: number;
  clientHeight: number;
}

/** How close to the end still counts as the bottom: sub-pixel rounding and a zoomed page, not a line of text */
export const AT_BOTTOM_PX = 8;

export const isAtBottom = ({ scrollTop, scrollHeight, clientHeight }: ScrollMetrics) =>
  scrollHeight - scrollTop - clientHeight <= AT_BOTTOM_PX;

/**
 * Whether the transcript keeps to its latest item after a scroll. Reaching the bottom, by any means,
 * takes hold. Only the reader moving up lets go: output that follows and a row that grows move the
 * list down or not at all, so a tap, a late-measured row or a list correction never lets go of a
 * reader who is still at the bottom.
 */
export function followAfterScroll(following: boolean, previousTop: number, now: ScrollMetrics, byReader: boolean): boolean {
  if (isAtBottom(now)) return true;
  if (byReader && now.scrollTop < previousTop) return false;
  return following;
}

/** Items after the one the reader last had at the bottom; 0 when that item is no longer shown (the view was filtered) */
export function countNewItems(items: readonly { key: string }[], lastSeenKey: string | null): number {
  if (lastSeenKey === null) return 0;
  for (let i = items.length - 1; i >= 0; i--)
    if (items[i].key === lastSeenKey) return items.length - 1 - i;
  return 0;
}
