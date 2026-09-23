import { describe, expect, it } from 'vitest';
import { normaliseStatusTokens, parseStatusFilter, serialiseStatusFilter, statusFilterHasFav } from '../app/utils/mediaStatusFilter';

describe('media status filter tokens', () => {
  it('reads an empty or absent value as no status filter', () => {
    expect(parseStatusFilter('')).toEqual([]);
    expect(parseStatusFilter(null)).toEqual([]);
    expect(parseStatusFilter(undefined)).toEqual([]);
  });

  it('reads an old single-value URL as a one-element list', () => {
    expect(parseStatusFilter('completed')).toEqual(['completed']);
    expect(parseStatusFilter('ignore')).toEqual(['ignore']);
  });

  it('reads a comma list in canonical order whatever order it arrives in', () => {
    expect(parseStatusFilter('nostatus,planning')).toEqual(['planning', 'nostatus']);
    expect(parseStatusFilter(' Planning , NOSTATUS ')).toEqual(['planning', 'nostatus']);
  });

  it('drops legacy and unknown tokens', () => {
    expect(parseStatusFilter('none')).toEqual([]);
    expect(parseStatusFilter('fav')).toEqual([]);
    expect(parseStatusFilter('planning,bogus,planning')).toEqual(['planning']);
  });

  it('detects the legacy fav token so it can become the favourite flag', () => {
    expect(statusFilterHasFav('fav')).toBe(true);
    expect(statusFilterHasFav('planning')).toBe(false);
    expect(statusFilterHasFav(null)).toBe(false);
  });

  it('serialises to a stable comma list, or nothing when empty', () => {
    expect(serialiseStatusFilter(['nostatus', 'planning'])).toBe('planning,nostatus');
    expect(serialiseStatusFilter(['planning', 'nostatus'])).toBe('planning,nostatus');
    expect(serialiseStatusFilter([])).toBeNull();
  });

  it('round-trips through the URL value', () => {
    const tokens = normaliseStatusTokens(['ignore', 'dropped', 'ongoing']);
    expect(parseStatusFilter(serialiseStatusFilter(tokens))).toEqual(tokens);
  });
});
