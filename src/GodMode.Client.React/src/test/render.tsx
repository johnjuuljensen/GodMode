/**
 * Renders a component into jsdom for a test marked `// @vitest-environment jsdom`, with React's act()
 * around every change, and dispatches the DOM events a user would.
 */
import { act, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

export interface Rendered {
  container: HTMLElement;
  unmount: () => void;
}

export async function render(ui: ReactNode): Promise<Rendered> {
  const container = document.body.appendChild(document.createElement('div'));
  const root = createRoot(container);
  await act(async () => root.render(ui));
  return {
    container,
    unmount: () => {
      act(() => root.unmount());
      container.remove();
    },
  };
}

/** Types into a controlled input or textarea: sets the value as the browser would and fires input. */
export async function typeInto(el: HTMLInputElement | HTMLTextAreaElement, value: string) {
  const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  await act(async () => {
    Object.getOwnPropertyDescriptor(proto, 'value')!.set!.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

/** Presses a key on the element (the page when none), returning the event to check defaultPrevented. */
export async function keyDown(target: Element | null, key: string, init: KeyboardEventInit = {}): Promise<KeyboardEvent> {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
  await act(async () => { (target ?? document.body).dispatchEvent(event); });
  return event;
}

/**
 * Focuses a button and presses a key on it as a browser does: the keydown, then, unless a handler
 * prevented it, Enter's default action, a click. jsdom runs no default actions of its own.
 */
export async function pressKey(button: HTMLButtonElement, key: string): Promise<KeyboardEvent> {
  await act(async () => button.focus());
  const event = await keyDown(button, key);
  if (key === 'Enter' && !event.defaultPrevented) await act(async () => button.click());
  return event;
}

/** Clicks an element, with React's act() around the change. */
export const click = (el: HTMLElement) => act(async () => el.click());
