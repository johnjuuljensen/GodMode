import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useAppStore, type SidebarItem } from '../../store';
import { KindLabel } from '../KindLabel/KindLabel';
import { deleteSession } from '../../deleteSession';

interface Props {
  item: SidebarItem;
  isSelected: boolean;
  onSelect: () => void;
}

/** How far a swipe to the left opens the row, showing Delete behind it (px). */
export const SWIPE_REVEAL_PX = 88;

/** How far a touch moves sideways before it is a swipe rather than a tap or a scroll (px). */
const SWIPE_SLOP_PX = 10;

/** The row menu's size, as Sidebar.css gives it, to keep it on the screen. */
const MENU_WIDTH_PX = 160;
const MENU_HEIGHT_PX = 44;

/**
 * A session in the list. Its delete is never a bare button (#325): on a phone the row is swiped to the
 * left to show Delete behind it; on a desktop its ⋯ button, or a right-click on it, opens a menu with Delete.
 */
export function ProjectItem({ item, isSelected, onSelect }: Props) {
  const { project, serverLabel } = item;
  const timeAgo = formatRelativeTime(project.UpdatedAt);
  const clientQuestion = useAppStore(s => s.projectQuestions[item.key]);
  const isMobile = useAppStore(s => s.isMobile);
  const isWaiting = project.State === 'WaitingInput' || clientQuestion;
  const stateStr = String(project.State ?? 'Idle');
  const stateLabel = isWaiting ? 'WAIT' : stateStr.slice(0, 4).toUpperCase();

  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const [offset, setOffset] = useState(0);
  const [revealed, setRevealed] = useState(false);
  const [dragging, setDragging] = useState(false);
  const touch = useRef<{ x: number; y: number; swiping: boolean; from: number } | null>(null);

  // A menu closes on a click or tap outside it, and on Escape
  useEffect(() => {
    if (!menu) return;
    const onDown = (e: MouseEvent | TouchEvent) => {
      if (!menuRef.current?.contains(e.target as Node)) setMenu(null);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setMenu(null); };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('touchstart', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('touchstart', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [menu]);

  const close = () => { setMenu(null); setRevealed(false); setOffset(0); };
  const handleDelete = () => { close(); void deleteSession(item.serverId, project); };

  // Kept on the screen: a menu opened near its right or bottom edge opens back from it. It is portalled to
  // the body (below), so these are the viewport's edges and not the sidebar's, which clips it otherwise
  const openMenuAt = (x: number, y: number) => setMenu({
    x: Math.max(8, Math.min(x, window.innerWidth - MENU_WIDTH_PX - 8)),
    y: Math.max(8, Math.min(y, window.innerHeight - MENU_HEIGHT_PX - 8)),
  });

  const onTouchStart = (e: React.TouchEvent) => {
    const t = e.touches[0];
    touch.current = { x: t.clientX, y: t.clientY, swiping: false, from: revealed ? -SWIPE_REVEAL_PX : 0 };
  };
  const onTouchMove = (e: React.TouchEvent) => {
    const start = touch.current;
    if (!start) return;
    const t = e.touches[0];
    const dx = t.clientX - start.x;
    const dy = t.clientY - start.y;
    if (!start.swiping) {
      // A scroll is a scroll: only a mostly sideways move becomes a swipe
      if (Math.abs(dy) > SWIPE_SLOP_PX && Math.abs(dy) > Math.abs(dx)) { touch.current = null; return; }
      if (Math.abs(dx) <= SWIPE_SLOP_PX) return;
      start.swiping = true;
      setDragging(true);
    }
    setOffset(Math.max(-SWIPE_REVEAL_PX, Math.min(0, start.from + dx)));
  };
  const onTouchEnd = () => {
    const start = touch.current;
    touch.current = null;
    if (!start?.swiping) return;
    setDragging(false);
    const open = offset < -SWIPE_REVEAL_PX / 2;
    setRevealed(open);
    setOffset(open ? -SWIPE_REVEAL_PX : 0);
  };

  const onRowClick = () => {
    // A tap on an opened row closes it, rather than opening the session
    if (revealed) { close(); return; }
    onSelect();
  };

  return (
    <div className={`project-item-wrapper ${revealed ? 'revealed' : ''}`}>
      {offset < 0 && (
        <button className="project-item-swipe-delete" onClick={handleDelete} style={{ width: SWIPE_REVEAL_PX }}>
          Delete
        </button>
      )}
      <div
        className={`project-item ${isSelected ? 'selected' : ''} ${isWaiting ? 'waiting' : ''} ${menu ? 'menu-open' : ''} ${offset ? 'swiped' : ''} ${dragging ? 'dragging' : ''}`}
        style={offset ? { transform: `translateX(${offset}px)` } : undefined}
        onClick={onRowClick}
        onContextMenu={e => { e.preventDefault(); openMenuAt(e.clientX, e.clientY); }}
        onTouchStart={onTouchStart}
        onTouchMove={onTouchMove}
        onTouchEnd={onTouchEnd}
        onTouchCancel={onTouchEnd}
      >
        <span className={`project-state-badge ${isWaiting ? 'WaitingInput' : project.State}`}>
          {stateLabel}
        </span>
        <div className="project-info">
          <div className="project-name-row">
            <div className="project-name">{project.Name}</div>
            <KindLabel kind={project.Kind} />
          </div>
          <div className="project-meta">
            {serverLabel && `${serverLabel} · `}{project.RootName && `${project.RootName} · `}{timeAgo}
            {isWaiting && project.CurrentQuestion && (
              <span className="project-question-hint" title={project.CurrentQuestion}>
                {' · '}{project.CurrentQuestion.length > 30
                  ? project.CurrentQuestion.slice(0, 30) + '...'
                  : project.CurrentQuestion}
              </span>
            )}
          </div>
        </div>
        {!isMobile && (
          <button
            className="project-item-menu-btn"
            title="More"
            aria-label={`More for ${project.Name}`}
            aria-haspopup="menu"
            onClick={e => {
              e.stopPropagation();
              const box = e.currentTarget.getBoundingClientRect();
              if (menu) setMenu(null); else openMenuAt(box.right, box.bottom);
            }}
          >⋯</button>
        )}
      </div>
      {/* Out of the sidebar: its backdrop-filter makes it the containing block of a fixed menu, and its overflow clips it */}
      {menu && createPortal(
        <div ref={menuRef} className="project-item-menu" role="menu" style={{ left: menu.x, top: menu.y }} onClick={e => e.stopPropagation()}>
          <button className="project-item-menu-item danger" role="menuitem" onClick={handleDelete}>
            {project.SharedFolder ? 'Delete' : 'Delete…'}
          </button>
        </div>,
        document.body,
      )}
    </div>
  );
}

function formatRelativeTime(dateStr: string): string {
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMin = Math.floor(diffMs / 60000);

  if (diffMin < 1) return 'just now';
  if (diffMin < 60) return `${diffMin}m ago`;
  const diffHr = Math.floor(diffMin / 60);
  if (diffHr < 24) return `${diffHr}h ago`;
  const diffDays = Math.floor(diffHr / 24);
  return `${diffDays}d ago`;
}
