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

  it('drops the abbreviation full stops in the short style', () => {
    expect(formatRelativeTime(new Date('2026-09-14T11:55:00Z'), now, 'short')).toBe('5 min ago');
    expect(formatRelativeTime(new Date('2026-09-14T09:00:00Z'), now, 'short')).toBe('3 hr ago');
  });

  it('names a single past unit instead of counting it', () => {
    expect(formatRelativeTime(new Date('2026-08-14T12:00:00Z'), now)).toBe('last month');
    expect(formatRelativeTime(new Date('2026-08-14T12:00:00Z'), now, 'short')).toBe('last mo');
    expect(formatRelativeTime(new Date('2026-09-07T12:00:00Z'), now, 'short')).toBe('last wk');
    expect(formatRelativeTime(new Date('2026-09-14T11:00:00Z'), now)).toBe('1 hour ago');
  });

  it('formats future dates', () => {
    expect(formatRelativeTime(new Date('2026-09-17T12:00:00Z'), now)).toBe('in 3 days');
    expect(formatRelativeTime(new Date('2026-09-15T12:00:00Z'), now)).toBe('tomorrow');
  });

  it('accepts ISO strings', () => {
    expect(formatRelativeTime('2026-09-13T12:00:00Z', now)).toBe('yesterday');
  });
});
