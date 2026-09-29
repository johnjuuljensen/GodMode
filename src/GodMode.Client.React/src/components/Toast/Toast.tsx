import { useEffect, useSyncExternalStore } from 'react';
import { dismissToast, durationOf, getToasts, subscribeToast, type OpenToast } from '../../toast';
import './Toast.css';

/** Renders the toasts showToast opened, stacked, each until its time is up, it is dismissed, or its action is tapped. Mount once, in the Shell. */
export function Toast() {
  const toasts = useSyncExternalStore(subscribeToast, getToasts);
  if (!toasts.length) return null;

  return (
    <div className="toast-stack">
      {toasts.map(toast => <ToastItem key={toast.id} toast={toast} />)}
    </div>
  );
}

function ToastItem({ toast }: { toast: OpenToast }) {
  useEffect(() => {
    const timer = setTimeout(() => dismissToast(toast.id), durationOf(toast));
    return () => clearTimeout(timer);
  }, [toast]);

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
