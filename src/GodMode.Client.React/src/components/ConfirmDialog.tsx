import { useEffect, useRef, useSyncExternalStore } from 'react';
import { getOpenConfirm, subscribeConfirm } from '../confirmDialog';
import './ConfirmDialog.css';

/** Renders the question `askConfirm` opened. Mount once, in the Shell. */
export function ConfirmDialog() {
  const open = useSyncExternalStore(subscribeConfirm, getOpenConfirm);
  const cancelRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    // Focus the safe answer, so Enter on a keyboard never confirms by accident
    cancelRef.current?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') open.resolve(null); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open]);

  if (!open) return null;
  const { request, resolve } = open;

  return (
    <div className="modal-overlay confirm-overlay" onClick={() => resolve(null)}>
      <div
        className="modal confirm-dialog"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
        onClick={e => e.stopPropagation()}
      >
        <h2 id="confirm-dialog-title">{request.title}</h2>
        {request.message && <p className="confirm-dialog-message">{request.message}</p>}
        <div className="confirm-dialog-actions">
          <button ref={cancelRef} className="btn btn-secondary" onClick={() => resolve(null)}>
            {request.cancelLabel ?? 'Cancel'}
          </button>
          {request.choices.map(choice => (
            <button key={choice.value} className={`btn btn-${choice.tone ?? 'primary'}`} onClick={() => resolve(choice.value)}>
              {choice.label}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
