/** When the transcript keeps to its latest item, and how many items the reader has not seen (#134) */
import { describe, expect, it } from 'vitest';
import { AT_BOTTOM_PX, countNewItems, followAfterScroll, isAtBottom } from './transcriptFollow';

// A 1000px list in a 400px viewport: 600 is the bottom
const at = (scrollTop: number, scrollHeight = 1000) => ({ scrollTop, scrollHeight, clientHeight: 400 });

describe('isAtBottom', () => {
  it('is the end, give or take rounding', () => {
    expect(isAtBottom(at(600))).toBe(true);
    expect(isAtBottom(at(600 - AT_BOTTOM_PX))).toBe(true);
    expect(isAtBottom(at(600 - AT_BOTTOM_PX - 1))).toBe(false);
  });

  it('is always true for a list shorter than its viewport', () => {
    expect(isAtBottom(at(0, 300))).toBe(true);
  });
});

describe('followAfterScroll', () => {
  it('lets go when the reader scrolls up', () => {
    expect(followAfterScroll(true, 600, at(500), true)).toBe(false);
  });

  it('holds when the list moves up without the reader: a correction, a collapsed row', () => {
    expect(followAfterScroll(true, 600, at(500), false)).toBe(true);
  });

  it('holds when the reader scrolls down, or taps, short of the bottom', () => {
    expect(followAfterScroll(true, 400, at(500), true)).toBe(true);
    expect(followAfterScroll(true, 500, at(500), true)).toBe(true);
  });

  it('holds a reader at the bottom whatever moved: a row that grew and was re-pinned', () => {
    expect(followAfterScroll(true, 600, at(1000, 1400), true)).toBe(true);
  });

  it('takes hold again once the reader is back at the bottom, however they got there', () => {
    expect(followAfterScroll(false, 200, at(600), true)).toBe(true);
    expect(followAfterScroll(false, 200, at(600), false)).toBe(true);
  });

  it('stays let go while the reader is up in the history', () => {
    expect(followAfterScroll(false, 200, at(300), true)).toBe(false);
    expect(followAfterScroll(false, 200, at(300), false)).toBe(false);
    expect(followAfterScroll(false, 300, at(200), false)).toBe(false);
  });
});

describe('countNewItems', () => {
  const items = ['a', 'b', 'c', 'd'].map(key => ({ key }));

  it('counts the items after the last one seen', () => {
    expect(countNewItems(items, 'b')).toBe(2);
    expect(countNewItems(items, 'd')).toBe(0);
  });

  it('is 0 when nothing was seen or the item seen is filtered out', () => {
    expect(countNewItems(items, null)).toBe(0);
    expect(countNewItems(items, 'gone')).toBe(0);
  });
});
