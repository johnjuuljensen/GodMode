// @vitest-environment jsdom
/**
 * The transcript's scroll behaviour (#134) against the events a reader makes: output follows while
 * they are at the bottom, and once they scroll up nothing moves and a button counts what is new.
 * Virtuoso is replaced by a plain scroller whose size the test sets, since jsdom lays nothing out.
 */
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, createRef, type ReactNode, type Ref } from 'react';
import { createRoot, type Root } from 'react-dom/client';
// For its act() environment: this test keeps its own root, to re-render the list with more items
import '../../test/render';
import type { TranscriptItem } from '../../signalr/parseMessage';
import { TranscriptList, type TranscriptListHandle } from './TranscriptList';

const virtuoso = vi.hoisted(() => ({
  followOutput: undefined as ((atBottom: boolean) => unknown) | undefined,
  totalListHeightChanged: undefined as ((height: number) => void) | undefined,
  scrollToIndex: undefined as unknown as Mock<(location: unknown) => void>,
}));

vi.mock('react-virtuoso', async () => {
  const { useEffect, useImperativeHandle, useRef } = await import('react');
  interface StubProps {
    ref?: Ref<unknown>;
    scrollerRef?: (el: HTMLElement | null) => void;
    data: TranscriptItem[];
    itemContent: (index: number, item: TranscriptItem) => ReactNode;
    className?: string;
    followOutput?: (atBottom: boolean) => unknown;
    totalListHeightChanged?: (height: number) => void;
  }
  return {
    Virtuoso: ({ ref, scrollerRef, data, itemContent, className, followOutput, totalListHeightChanged }: StubProps) => {
      const el = useRef<HTMLDivElement>(null);
      useEffect(() => { Object.assign(virtuoso, { followOutput, totalListHeightChanged }); });
      useImperativeHandle(ref, () => ({ scrollToIndex: (location: unknown) => virtuoso.scrollToIndex(location) }));
      useEffect(() => { scrollerRef?.(el.current); return () => scrollerRef?.(null); }, [scrollerRef]);
      return <div ref={el} className={className}>{data.map((item, i) => <div key={item.key}>{itemContent(i, item)}</div>)}</div>;
    },
  };
});

// The scroller is 400px tall over content the test sizes: the bottom is scrollHeight - viewport
let scrollHeight = 2000;
let viewport = 400;
const bottom = () => scrollHeight - viewport;

// jsdom has no ResizeObserver: this one reports when the test resizes the scroller
const observers = new Set<() => void>();
globalThis.ResizeObserver = class {
  private readonly notify: () => void;
  constructor(callback: ResizeObserverCallback) { this.notify = () => callback([], this); }
  observe() { observers.add(this.notify); }
  unobserve() {}
  disconnect() { observers.delete(this.notify); }
};
let container: HTMLElement;
let root: Root;
const handle = createRef<TranscriptListHandle>();

const reply = (n: number): TranscriptItem => ({ kind: 'assistantText', key: `a${n}`, text: `reply ${n}` });
const replies = (count: number) => Array.from({ length: count }, (_, i) => reply(i));

const show = (count: number) => act(async () => root.render(<TranscriptList ref={handle} items={replies(count)} />));

const scroller = () => container.querySelector<HTMLElement>('.transcript-list')!;
const button = () => container.querySelector<HTMLButtonElement>('.transcript-jump');
const follows = () => virtuoso.followOutput?.(false);
const fire = (event: Event, target: EventTarget = scroller()) => act(async () => { target.dispatchEvent(event); });
const resize = (to: number) => act(async () => { viewport = to; observers.forEach(notify => notify()); });
const grow = (to: number) => act(async () => { scrollHeight = to; virtuoso.totalListHeightChanged?.(to); });

function scrollNow(top: number) {
  scroller().scrollTop = top;
  scroller().dispatchEvent(new Event('scroll'));
}
const scrollTo = (top: number) => act(async () => scrollNow(top));

async function wheelUp(to: number) {
  await fire(new WheelEvent('wheel', { deltaY: -100, bubbles: true }));
  await scrollTo(to);
}

beforeEach(async () => {
  vi.useFakeTimers({ toFake: ['performance'] });
  scrollHeight = 2000;
  viewport = 400;
  // Scrolling to the latest lands at the bottom, as Virtuoso does
  virtuoso.scrollToIndex = vi.fn(() => scrollNow(bottom()));
  container = document.body.appendChild(document.createElement('div'));
  root = createRoot(container);
  await show(20);
  Object.defineProperty(scroller(), 'scrollHeight', { configurable: true, get: () => scrollHeight });
  Object.defineProperty(scroller(), 'clientHeight', { configurable: true, get: () => viewport });
  await scrollTo(bottom());
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
  vi.useRealTimers();
});

describe('at the bottom', () => {
  it('follows new output and shows no button', async () => {
    await show(25);
    expect(follows()).toBe('auto');
    expect(button()).toBeNull();
  });

  it('stays pinned when a row grows after a tap (#173 review: a tap let go of the list)', async () => {
    await fire(new PointerEvent('pointerdown', { bubbles: true, pointerType: 'mouse' }));
    await fire(new PointerEvent('pointerup', { bubbles: true }), window);
    await fire(new Event('touchstart', { bubbles: true }));
    await fire(new Event('touchend', { bubbles: true }), window);
    await grow(2600);
    expect(virtuoso.scrollToIndex).toHaveBeenCalledWith({ index: 'LAST', align: 'end' });
    expect(scroller().scrollTop).toBe(bottom());
    expect(follows()).toBe('auto');
    expect(button()).toBeNull();
  });

  it('stays pinned when the list gets shorter: the input grew, a keyboard opened', async () => {
    await resize(300);
    expect(virtuoso.scrollToIndex).toHaveBeenCalledWith({ index: 'LAST', align: 'end' });
    expect(scroller().scrollTop).toBe(bottom());
    expect(button()).toBeNull();
  });

  it('does not let go when the list moves up without the reader', async () => {
    await scrollTo(bottom() - 200);
    expect(follows()).toBe('auto');
    expect(button()).toBeNull();
  });
});

describe('scrolled up', () => {
  it('lets go on a wheel turn up: new output does not move the list, and the button counts it', async () => {
    await wheelUp(800);
    expect(follows()).toBe(false);
    expect(button()?.textContent).toBe('');
    expect(button()?.getAttribute('aria-label')).toBe('Scroll to the latest message');

    await show(21);
    expect(button()?.textContent).toBe('1 new message');
    await show(23);
    expect(button()?.textContent).toBe('3 new messages');
    expect(scroller().scrollTop).toBe(800);
  });

  it('lets go on a touch drag up and on a key', async () => {
    await fire(new Event('touchstart', { bubbles: true }));
    await scrollTo(1000);
    expect(button()).not.toBeNull();
    await fire(new Event('touchend', { bubbles: true }), window);
    await scrollTo(bottom());
    expect(button()).toBeNull();

    await fire(new KeyboardEvent('keydown', { key: 'PageUp', bubbles: true }));
    await scrollTo(1200);
    expect(button()).not.toBeNull();
  });

  it('does not count a scroll long after the reader stopped as theirs', async () => {
    await fire(new WheelEvent('wheel', { deltaY: -100, bubbles: true }));
    vi.advanceTimersByTime(1500);
    await scrollTo(1000);
    expect(button()).toBeNull();
  });

  it('does not re-pin a row that grows, or the list getting shorter', async () => {
    await wheelUp(800);
    await grow(2600);
    await resize(300);
    expect(virtuoso.scrollToIndex).not.toHaveBeenCalled();
    expect(scroller().scrollTop).toBe(800);
  });

  it('takes hold again once the reader scrolls back to the bottom', async () => {
    await wheelUp(800);
    await show(22);
    await scrollTo(bottom());
    expect(button()).toBeNull();
    expect(follows()).toBe('auto');
  });

  it('the button scrolls to the latest and follows again', async () => {
    await wheelUp(800);
    await show(22);
    await fire(new MouseEvent('click', { bubbles: true }), button()!);
    expect(virtuoso.scrollToIndex).toHaveBeenCalledWith({ index: 'LAST', align: 'end' });
    expect(scroller().scrollTop).toBe(bottom());
    expect(button()).toBeNull();
    expect(follows()).toBe('auto');
  });

  it("the reader's own message scrolls to the latest", async () => {
    await wheelUp(800);
    await act(async () => handle.current?.scrollToLatest());
    expect(scroller().scrollTop).toBe(bottom());
    expect(button()).toBeNull();
    expect(follows()).toBe('auto');
  });
});
