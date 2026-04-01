import { useState } from 'react';

/** Toggle switch */
export function Toggle({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <button
      className={`settings-toggle ${checked ? 'on' : 'off'}`}
      onClick={() => onChange(!checked)}
      type="button"
    >
      <div className="settings-toggle-knob" />
    </button>
  );
}

/** Small icon button (edit, delete, etc.) */
export function IconBtn({ title, svg, danger, onClick }: {
  title: string;
  svg: string;
  danger?: boolean;
  onClick: (e: React.MouseEvent) => void;
}) {
  return (
    <button
      className={`settings-icon-btn${danger ? ' danger' : ''}`}
      title={title}
      onClick={e => { e.stopPropagation(); onClick(e); }}
      type="button"
    >
      <svg width={15} height={15} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.4} strokeLinecap="round" strokeLinejoin="round" dangerouslySetInnerHTML={{ __html: svg }} />
    </button>
  );
}

// Common SVG icon paths
export const ICON_EDIT  = '<path d="M11.5 2.5a1.414 1.414 0 0 1 2 2L5 13H3v-2L11.5 2.5Z"/>';
export const ICON_TRASH = '<path d="M3 4h10M6 4V3h4v1M5 4l.5 9h5L11 4"/>';
export const ICON_COPY  = '<path d="M5 4V3a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v7a1 1 0 0 1-1 1h-1"/><rect x="3" y="6" width="8" height="8" rx="1"/>';

/** Inline delete confirmation row */
export function DeleteConfirm({ label, onConfirm, onCancel }: {
  label?: string;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <div className="settings-confirm">
      {label && <span className="settings-confirm-label">{label}</span>}
      <button className="btn btn-sm" onClick={e => { e.stopPropagation(); onCancel(); }}>Cancel</button>
      <button className="btn btn-danger btn-sm" onClick={e => { e.stopPropagation(); onConfirm(); }}>Delete</button>
    </div>
  );
}

/** 36px colored icon box */
export function ItemIcon({ children, variant }: { children: React.ReactNode; variant?: string }) {
  return (
    <div className={`settings-item-icon${variant ? ` ${variant}` : ''}`}>
      {children}
    </div>
  );
}

/** Wrapper for the standard row actions pattern: shows delete icon, then confirm on click */
export function RowDelete({ onDelete }: { onDelete: () => void }) {
  const [confirm, setConfirm] = useState(false);
  if (confirm) {
    return <DeleteConfirm onConfirm={onDelete} onCancel={() => setConfirm(false)} />;
  }
  return <IconBtn title="Delete" svg={ICON_TRASH} danger onClick={() => setConfirm(true)} />;
}
