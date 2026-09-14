import { describe, expect, it } from 'vitest';
import { formatRelativeTime } from '~/utils/relativeTime';

describe('formatRelativeTime', () => {
  const now = new Date('2026-09-14T12:00:00Z');

  it('returns just now under a minute', () => {
    expect(formatRelativeTime(new Date('2026-09-14T11:59:30Z'), now)).toBe('just now');
  });

  it('picks the largest fitting unit', () => {
    expect(formatRelativeTime(new Date('2026-09-14T09:00:00Z'), now)).toBe('3 hours ago');
    expect(formatRelativeTime(new Date('2026-09-11T12:00:00Z'), now)).toBe('3 days ago');
    expect(formatRelativeTime(new Date('2026-08-24T12:00:00Z'), now)).toBe('3 weeks ago');
    expect(formatRelativeTime(new Date('2026-03-14T12:00:00Z'), now)).toBe('6 months ago');
  });

  it('accepts ISO strings', () => {
    expect(formatRelativeTime('2026-09-13T12:00:00Z', now)).toBe('yesterday');
  });
});
