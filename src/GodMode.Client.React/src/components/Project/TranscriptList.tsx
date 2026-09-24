import { useCallback, useEffect, useImperativeHandle, useLayoutEffect, useRef, useState, type Ref } from 'react';
import { Virtuoso, type VirtuosoHandle } from 'react-virtuoso';
import type { TranscriptItem } from '../../signalr/parseMessage';
import { ChatMessage } from './ChatMessage';
import { countNewItems, followAfterScroll } from './transcriptFollow';
import './Transcript.css';

export interface TranscriptListHandle {
  /** Scrolls to the latest item and keeps to it, as the reader's own message should */
  scrollToLatest: () => void;
}

interface Props {
  items: TranscriptItem[];
  ref?: Ref<TranscriptListHandle>;
}

const Spacer = () => <div className="transcript-spacer" />;
const components = { Header: Spacer, Footer: Spacer };
const itemKey = (_: number, item: TranscriptItem) => item.key;
// How long a scroll after a wheel turn, a key or a lifted finger is still the reader's: smooth and momentum scrolling
const READER_SCROLL_MS = 1000;

/**
 * A project's transcript, virtualized: only the rows near the viewport are in the DOM. It opens at
 * the latest item and keeps to it while the reader is at the bottom, as output arrives and as rows
 * are measured. Once the reader scrolls up nothing moves the list: new items show a "new messages"
 * button instead, which takes them back to the latest. It is remounted (a `key`) for each project.
 */
export function TranscriptList({ items, ref }: Props) {
  // Open rows by item key: kept here, not in the row, so a row scrolled away and back stays open
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());
  const toggle = useCallback((key: string) => setExpanded(prev => {
    const next = new Set(prev);
    if (!next.delete(key)) next.add(key);
    return next;
  }), []);

  const virtuoso = useRef<VirtuosoHandle>(null);
  const itemsRef = useRef(items);
  useLayoutEffect(() => { itemsRef.current = items; }, [items]);

  // Whether the list keeps to the latest item; when it lets go, the item the reader last had at the bottom
  const [following, setFollowing] = useState(true);
  const [lastSeenKey, setLastSeenKey] = useState<string | null>(null);
  const followingRef = useRef(true);
  const follow = useCallback((next: boolean) => {
    if (next === followingRef.current) return;
    followingRef.current = next;
    setFollowing(next);
    setLastSeenKey(next ? null : itemsRef.current.at(-1)?.key ?? null);
  }, []);

  const scrollToLatest = useCallback(() => {
    follow(true);
    virtuoso.current?.scrollToIndex({ index: 'LAST', align: 'end' });
  }, [follow]);
  useImperativeHandle(ref, () => ({ scrollToLatest }), [scrollToLatest]);

  const followOutput = useCallback(() => (followingRef.current ? 'auto' as const : false), []);
  // Rows are estimated until measured, and grow when opened: a taller row at the bottom would leave the list short of it
  const onHeightChanged = useCallback(() => {
    if (followingRef.current) virtuoso.current?.scrollToIndex({ index: 'LAST', align: 'end' });
  }, []);

  // A scroll is the reader's while a finger or mouse button is down, and briefly after it or a wheel turn or key
  const [scroller, setScroller] = useState<HTMLElement | null>(null);
  const scrollerRef = useCallback((el: HTMLElement | Window | null) => setScroller(el instanceof HTMLElement ? el : null), []);
  useEffect(() => {
    if (!scroller) return;
    let lastTop = scroller.scrollTop;
    let held = false;
    let readerUntil = 0;
    const briefly = () => { readerUntil = performance.now() + READER_SCROLL_MS; };
    const hold = () => { held = true; };
    const letGo = () => { if (held) { held = false; briefly(); } };
    // A touch's pointer is cancelled as soon as it pans, so touches are tracked by the touch events
    const holdPointer = (e: PointerEvent) => { if (e.pointerType !== 'touch') hold(); };
    const onScroll = () => {
      follow(followAfterScroll(followingRef.current, lastTop, scroller, held || performance.now() < readerUntil));
      lastTop = scroller.scrollTop;
    };
    const listeners: [EventTarget, string, EventListener][] = [
      [scroller, 'scroll', onScroll],
      [scroller, 'wheel', briefly],
      [scroller, 'keydown', briefly],
      [scroller, 'pointerdown', holdPointer as EventListener],
      [window, 'pointerup', letGo],
      [window, 'pointercancel', letGo],
      [scroller, 'touchstart', hold],
      [window, 'touchend', letGo],
      [window, 'touchcancel', letGo],
    ];
    for (const [target, type, listener] of listeners) target.addEventListener(type, listener, { passive: true });
    // The list gets shorter as the input grows or an on-screen keyboard opens: a reader at the bottom stays there
    let lastHeight = scroller.clientHeight;
    const resized = new ResizeObserver(() => {
      if (scroller.clientHeight === lastHeight) return;
      lastHeight = scroller.clientHeight;
      if (followingRef.current) virtuoso.current?.scrollToIndex({ index: 'LAST', align: 'end' });
    });
    resized.observe(scroller);
    return () => {
      for (const [target, type, listener] of listeners) target.removeEventListener(type, listener);
      resized.disconnect();
    };
  }, [scroller, follow]);

  const renderItem = useCallback((_: number, item: TranscriptItem) => (
    <div className="transcript-row">
      <ChatMessage
        item={item}
        expanded={expanded.has(item.key)}
        onToggle={toggle}
        expandedKeys={item.kind === 'toolCall' && item.children.length > 0 ? expanded : undefined}
      />
    </div>
  ), [expanded, toggle]);

  const newCount = following ? 0 : countNewItems(items, lastSeenKey);

  return (
    <div className="transcript">
      <Virtuoso
        ref={virtuoso}
        scrollerRef={scrollerRef}
        className="project-messages transcript-list"
        data={items}
        computeItemKey={itemKey}
        itemContent={renderItem}
        components={components}
        initialTopMostItemIndex={{ index: 'LAST', align: 'end' }}
        followOutput={followOutput}
        totalListHeightChanged={onHeightChanged}
        // Most rows are one-line tool calls: estimating that height keeps scroll corrections small
        defaultItemHeight={34}
        skipAnimationFrameInResizeObserver
        increaseViewportBy={{ top: 300, bottom: 300 }}
      />
      {!following && (
        <button
          type="button"
          className={`transcript-jump ${newCount > 0 ? 'has-new' : ''}`}
          onClick={scrollToLatest}
          aria-label={newCount > 0 ? `${newCount} new ${newCount === 1 ? 'message' : 'messages'}, scroll to the latest` : 'Scroll to the latest message'}
          title="Scroll to the latest message"
        >
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <line x1="12" y1="5" x2="12" y2="19" /><polyline points="19 12 12 19 5 12" />
          </svg>
          {newCount > 0 && <span>{newCount} new {newCount === 1 ? 'message' : 'messages'}</span>}
        </button>
      )}
    </div>
  );
}
