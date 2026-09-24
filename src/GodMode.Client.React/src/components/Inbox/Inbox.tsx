import { useEffect, useMemo, useState } from 'react';
import { useAppStore, projectKey } from '../../store';
import { InboxItem } from './InboxItem';
import './Inbox.css';

const COLLAPSED_KEY = 'godmode-inbox-collapsed';
const CLOCK_MS = 30_000;

interface Props {
  /** 'screen' fills the phone's home screen; 'pane' sits above the project list or tile grid, and collapses. */
  variant: 'screen' | 'pane';
}

/** What needs the user, across every connected server, oldest first. */
export function Inbox({ variant }: Props) {
  const attention = useAppStore(s => s.attention);
  const serverConnections = useAppStore(s => s.serverConnections);
  const selectProject = useAppStore(s => s.selectProject);
  const focus = useAppStore(s => s.inboxFocus);
  const [now, setNow] = useState(Date.now);
  const [collapsed, setCollapsed] = useState(() => {
    try { return localStorage.getItem(COLLAPSED_KEY) === 'true'; } catch { return false; }
  });

  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), CLOCK_MS);
    return () => clearInterval(id);
  }, []);

  // A tapped notification's item must be in view, so a collapsed pane opens (once per tap)
  const [focusSeen, setFocusSeen] = useState(focus);
  if (focus !== focusSeen) {
    setFocusSeen(focus);
    if (focus) setCollapsed(false);
  }

  const serverNames = useMemo(
    () => Object.fromEntries(serverConnections.map(c => [c.serverInfo.Id, c.serverInfo.Name])),
    [serverConnections],
  );
  const running = useMemo(
    () => serverConnections.flatMap(c => c.projects
      .filter(p => p.State === 'Running')
      .map(p => ({ serverId: c.serverInfo.Id, serverName: c.serverInfo.Name, project: p }))),
    [serverConnections],
  );

  const toggle = () => setCollapsed(c => {
    try { localStorage.setItem(COLLAPSED_KEY, String(!c)); } catch { /* not persisted */ }
    return !c;
  });

  const isPane = variant === 'pane';
  const open = !isPane || !collapsed;
  // A pane with nothing in it stays one line, so the project list keeps the room
  const showEmpty = open && attention.length === 0 && !isPane;

  return (
    <section className={`inbox inbox-${variant}`} aria-label="Needs you">
      <header className="inbox-header">
        {isPane ? (
          <button className="inbox-title inbox-toggle" onClick={toggle} aria-expanded={open}>
            <svg className={`inbox-chevron ${open ? 'expanded' : ''}`} width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
              <polyline points="6 9 12 15 18 9" />
            </svg>
            Needs you
          </button>
        ) : (
          <h2 className="inbox-title">Needs you</h2>
        )}
        <span className={`inbox-count ${attention.length > 0 ? 'active' : ''}`}>{attention.length}</span>
      </header>

      {open && attention.length > 0 && (
        <div className="inbox-list">
          {attention.map(item => (
            <InboxItem
              key={projectKey(item.serverId, item.ProjectId)}
              item={item}
              serverName={serverNames[item.serverId] ?? item.serverId}
              now={now}
              focus={focus?.key === projectKey(item.serverId, item.ProjectId) ? focus.seq : undefined}
            />
          ))}
        </div>
      )}

      {open && attention.length === 0 && isPane && <div className="inbox-empty-line">Nothing needs you.</div>}

      {showEmpty && (
        <div className="inbox-empty">
          <p className="inbox-empty-title">Nothing needs you.</p>
          {running.length > 0 && (
            <>
              <p className="inbox-empty-sub">Running</p>
              <ul className="inbox-running">
                {running.map(r => (
                  <li key={projectKey(r.serverId, r.project.Id)}>
                    <button className="inbox-running-item" onClick={() => selectProject(r.serverId, r.project.Id)}>
                      <span className="inbox-running-dot" />
                      <span className="inbox-running-name">{r.project.Name}</span>
                      <span className="inbox-item-meta">{r.serverName}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      )}
    </section>
  );
}

export type HomeTab = 'inbox' | 'projects';

/** The phone's switch between the inbox (home) and the project list. */
export function HomeTabBar({ tab, onChange }: { tab: HomeTab; onChange: (tab: HomeTab) => void }) {
  const count = useAppStore(s => s.attention.length);
  return (
    <nav className="home-tabs">
      <button className={`home-tab ${tab === 'inbox' ? 'active' : ''}`} onClick={() => onChange('inbox')}>
        Needs you{count > 0 && <span className="inbox-count active">{count}</span>}
      </button>
      <button className={`home-tab ${tab === 'projects' ? 'active' : ''}`} onClick={() => onChange('projects')}>
        Projects
      </button>
    </nav>
  );
}
