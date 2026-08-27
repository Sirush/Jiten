import { describe, expect, it } from 'vitest';
import { appendSkippedPollId, parseSkippedPollIds } from '../app/utils/skippedPolls';

describe('parseSkippedPollIds', () => {
  it('parses a stored array of ids', () => {
    expect(parseSkippedPollIds('[1,2,3]')).toEqual([1, 2, 3]);
  });

  it('returns empty for missing or corrupt values', () => {
    expect(parseSkippedPollIds(null)).toEqual([]);
    expect(parseSkippedPollIds('')).toEqual([]);
    expect(parseSkippedPollIds('not json')).toEqual([]);
    expect(parseSkippedPollIds('{"a":1}')).toEqual([]);
  });

  it('drops non-integer entries', () => {
    expect(parseSkippedPollIds('[1,"2",null,2.5,3]')).toEqual([1, 3]);
  });
});

describe('appendSkippedPollId', () => {
  it('appends a new id', () => {
    expect(appendSkippedPollId([1, 2], 3)).toEqual([1, 2, 3]);
  });

  it('moves an existing id to the end instead of duplicating', () => {
    expect(appendSkippedPollId([1, 2, 3], 2)).toEqual([1, 3, 2]);
  });

  it('caps the list at 500, dropping the oldest', () => {
    const full = Array.from({ length: 500 }, (_, i) => i + 1);
    const next = appendSkippedPollId(full, 999);
    expect(next).toHaveLength(500);
    expect(next[0]).toBe(2);
    expect(next[499]).toBe(999);
  });
});
