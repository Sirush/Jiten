import { describe, expect, it } from 'vitest';
import { insertionIndex, pickDropList } from '../app/composables/useTouchReorderMulti';

describe('pickDropList', () => {
  it('returns the list directly under the pointer regardless of the source', () => {
    expect(pickDropList('back', 'front', true)).toBe('back');
    expect(pickDropList('back', 'front', false)).toBe('back');
  });

  it('falls back to the nearest list when the source is itself a drop target (row reorder)', () => {
    expect(pickDropList(null, 'front', true)).toBe('front');
  });

  it('resolves to null for a palette-origin drag released outside every panel', () => {
    // Source is not a drop target (e.g. a palette chip); a stray release must not insert anywhere.
    expect(pickDropList(null, 'front', false)).toBeNull();
  });

  it('resolves to null when there is no candidate at all', () => {
    expect(pickDropList(null, null, true)).toBeNull();
    expect(pickDropList(null, null, false)).toBeNull();
  });
});

describe('insertionIndex', () => {
  const rect = (left: number, top: number) => ({ left, top, width: 100, height: 30, bottom: top + 30 });
  // Two wrapped rows: [a b c] at y 0-30, [d e] at y 40-70.
  const wrapped = [rect(0, 0), rect(110, 0), rect(220, 0), rect(0, 40), rect(110, 40)];

  it('compares horizontally within the row under the pointer', () => {
    expect(insertionIndex(wrapped, 10, 15, 'wrap')).toBe(0);
    expect(insertionIndex(wrapped, 170, 15, 'wrap')).toBe(2);
    expect(insertionIndex(wrapped, 60, 55, 'wrap')).toBe(4);
  });

  it('lands after the last item of a row when the pointer is past its right edge', () => {
    expect(insertionIndex(wrapped, 400, 15, 'wrap')).toBe(3);
    expect(insertionIndex(wrapped, 400, 55, 'wrap')).toBe(5);
  });

  it('treats the gap between rows as the start of the next row', () => {
    expect(insertionIndex(wrapped, 200, 35, 'wrap')).toBe(3);
  });

  it('keeps the vertical midpoint rule for stacked lists', () => {
    const stacked = [rect(0, 0), rect(0, 40)];
    expect(insertionIndex(stacked, 0, 10, 'vertical')).toBe(0);
    expect(insertionIndex(stacked, 0, 20, 'vertical')).toBe(1);
    expect(insertionIndex(stacked, 0, 80, 'vertical')).toBe(2);
  });
});
