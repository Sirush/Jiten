import { describe, expect, it } from 'vitest';
import { pickDropList } from '../app/composables/useTouchReorderMulti';

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
