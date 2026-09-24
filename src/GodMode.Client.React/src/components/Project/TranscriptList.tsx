import { useCallback, useState } from 'react';
import { Virtuoso } from 'react-virtuoso';
import type { TranscriptItem } from '../../signalr/parseMessage';
import { ChatMessage } from './ChatMessage';
import './Transcript.css';

interface Props {
  items: TranscriptItem[];
}

const Spacer = () => <div className="transcript-spacer" />;
const components = { Header: Spacer, Footer: Spacer };
const itemKey = (_: number, item: TranscriptItem) => item.key;

/**
 * A project's transcript, virtualized: only the rows near the viewport are in the DOM. It opens at
 * the latest item, and follows new output only while it is scrolled to the bottom.
 *
 * Scroll behaviour (#134) hooks in here: `atBottomStateChange` says when the reader leaves or
 * returns to the bottom (the moment to show or hide a "new messages" button), a `ref` gives
 * `scrollToIndex({ index: 'LAST' })` for that button, and `followOutput` decides whether new items
 * scroll the list. Remount it (a `key`) for each project.
 */
export function TranscriptList({ items }: Props) {
  // Open rows by item key: kept here, not in the row, so a row scrolled away and back stays open
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());
  const toggle = useCallback((key: string) => setExpanded(prev => {
    const next = new Set(prev);
    if (!next.delete(key)) next.add(key);
    return next;
  }), []);

  const renderItem = useCallback((_: number, item: TranscriptItem) => (
    <ChatMessage
      item={item}
      expanded={expanded.has(item.key)}
      onToggle={toggle}
      expandedKeys={item.kind === 'toolCall' && item.children.length > 0 ? expanded : undefined}
    />
  ), [expanded, toggle]);

  return (
    <Virtuoso
      className="project-messages"
      data={items}
      computeItemKey={itemKey}
      itemContent={renderItem}
      components={components}
      initialTopMostItemIndex={{ index: 'LAST', align: 'end' }}
      // Only while the reader is at the bottom: new output never pulls them away from what they read
      followOutput="auto"
      increaseViewportBy={{ top: 600, bottom: 600 }}
    />
  );
}
