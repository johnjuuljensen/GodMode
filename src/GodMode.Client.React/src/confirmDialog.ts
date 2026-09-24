// In-app replacement for window.confirm(), which MAUI's WebView does not show.
// `askConfirm` opens the dialog that <ConfirmDialog /> (mounted once, in the Shell) renders,
// and resolves with the value of the choice tapped, or null when it is dismissed.

export interface ConfirmChoice<T extends string> {
  label: string;
  value: T;
  tone?: 'primary' | 'danger' | 'secondary';
}

export interface ConfirmRequest<T extends string = string> {
  title: string;
  message?: string;
  /** Shown left to right; the last one is the default action */
  choices: readonly ConfirmChoice<T>[];
  /** Label of the button that dismisses; defaults to Cancel */
  cancelLabel?: string;
}

export interface OpenConfirm {
  request: ConfirmRequest;
  resolve: (value: string | null) => void;
}

let open: OpenConfirm | null = null;
const listeners = new Set<() => void>();

function setOpen(next: OpenConfirm | null) {
  open = next;
  listeners.forEach(l => l());
}

export function subscribeConfirm(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

export const getOpenConfirm = () => open;

/** Asks, and resolves with the chosen value, or null when dismissed. A second ask dismisses the first. */
export function askConfirm<T extends string>(request: ConfirmRequest<T>): Promise<T | null> {
  open?.resolve(null);
  return new Promise(resolve => {
    setOpen({
      request,
      resolve: value => {
        if (open?.request === request) setOpen(null);
        resolve(value as T | null);
      },
    });
  });
}

/** A yes/no question with one confirming button */
export async function confirmAction(title: string, confirmLabel: string, options?: { message?: string; tone?: 'primary' | 'danger' }): Promise<boolean> {
  const choice = await askConfirm({
    title,
    message: options?.message,
    choices: [{ label: confirmLabel, value: 'confirm', tone: options?.tone ?? 'primary' }],
  });
  return choice === 'confirm';
}
