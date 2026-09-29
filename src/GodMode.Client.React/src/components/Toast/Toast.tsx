import { useEffect, useSyncExternalStore } from 'react';
import { dismissToast, durationOf, getToast, subscribeToast } from '../../toast';
import './Toast.css';

/** Renders the toast showToast opened, until its time is up, it is dismissed, or its action is tapped. Mount once, in the Shell. */
export function Toast() {
  const toast = useSyncExternalStore(subscribeToast, getToast);

  useEffect(() => {
    if (!toast) return;
    const timer = setTimeout(() => dismissToast(toast.id), durationOf(toast));
    return () => clearTimeout(timer);
  }, [toast]);

  if (!toast) return null;
  const { action } = toast;

  return (
    <div className={`toast toast-${toast.tone ?? 'info'}`} role={toast.tone === 'error' ? 'alert' : 'status'}>
      <span className="toast-text">{toast.text}</span>
      {action && (
        <button className="toast-action" onClick={() => { dismissToast(toast.id); void action.run(); }}>
          {action.label}
        </button>
      )}
      <button className="toast-close" onClick={() => dismissToast(toast.id)} aria-label="Dismiss">×</button>
    </div>
  );
}
