import { useCallback, useRef, useState } from 'react';
import { Virtuoso, type VirtuosoHandle } from 'react-virtuoso';
import type { TranscriptItem } from '../../signalr/parseMessage';
import { ChatMessage } from './ChatMessage';
import './Transcript.css';

interface Props {
  items: TranscriptItem[];
}

const Spacer = () => <div className="transcript-spacer" />;
const components = { Header: Spacer, Footer: Spacer };
const itemKey = (_: number, item: TranscriptItem) => item.key;
const SCROLL_UP_KEYS = new Set(['ArrowUp', 'PageUp', 'Home']);

/**
 * A project's transcript, virtualized: only the rows near the viewport are in the DOM. It opens at
 * the latest item and stays there while the reader does, as output arrives and as rows are measured;
 * once the reader scrolls up, nothing moves the list until they are back at the bottom.
 *
 * Scroll behaviour (#134) hooks in here: `atBottomStateChange` says when the reader leaves or
 * returns to the bottom (the moment to show or hide a "new messages" button), `virtuoso` gives
 * `scrollToIndex({ index: 'LAST' })` for that button, and `followOutput` decides whether new items
 * scroll the list. It is remounted (a `key`) for each project.
 */
export function TranscriptList({ items }: Props) {
  // Open rows by item key: kept here, not in the row, so a row scrolled away and back stays open
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());
  const toggle = useCallback((key: string) => setExpanded(prev => {
    const next = new Set(prev);
    if (!next.delete(key)) next.add(key);
    return next;
  }), []);

  const virtuoso = useRef<VirtuosoHandle>(null);
  // Whether the list keeps to the bottom: from opening, until the reader scrolls up, and again once they are back
  const sticky = useRef(true);
  const release = useCallback(() => { sticky.current = false; }, []);
  const onAtBottom = useCallback((atBottom: boolean) => { if (atBottom) sticky.current = true; }, []);
  const followOutput = useCallback((atBottom: boolean) => (sticky.current || atBottom ? 'auto' as const : false), []);
  // Rows are estimated until measured: a tall one measured at the bottom (a long reply) would leave the list short of it
  const onHeightChanged = useCallback(() => {
    if (sticky.current) virtuoso.current?.scrollToIndex({ index: 'LAST', align: 'end' });
  }, []);

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

  return (
    <Virtuoso
      ref={virtuoso}
      className="project-messages transcript-list"
      data={items}
      computeItemKey={itemKey}
      itemContent={renderItem}
      components={components}
      initialTopMostItemIndex={{ index: 'LAST', align: 'end' }}
      followOutput={followOutput}
      atBottomStateChange={onAtBottom}
      totalListHeightChanged={onHeightChanged}
      onWheel={e => { if (e.deltaY < 0) release(); }}
      onTouchMove={release}
      onPointerDown={release}
      onKeyDown={e => { if (SCROLL_UP_KEYS.has(e.key)) release(); }}
      // Most rows are one-line tool calls: estimating that height keeps scroll corrections small
      defaultItemHeight={34}
      skipAnimationFrameInResizeObserver
      increaseViewportBy={{ top: 300, bottom: 300 }}
    />
  );
}
