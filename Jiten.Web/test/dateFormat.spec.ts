import { describe, expect, it } from 'vitest';
import {
  formatDateLong,
  formatDateOmitCurrentYear,
  formatDateShort,
  formatDateTime,
  formatDayMonth,
  formatDayMonthTime,
  formatMonthShort,
  formatMonthYear,
  formatWeekdayDayMonth,
} from '~/utils/dateFormat';

describe('dateFormat', () => {
  const date = new Date(2026, 8, 7, 9, 5);

  it('puts the day before the month and abbreviates September as Sep', () => {
    expect(formatDateLong(date)).toBe('7 September 2026');
    expect(formatDateShort(date)).toBe('7 Sep 2026');
    expect(formatDayMonth(date)).toBe('7 Sep');
    expect(formatDayMonth(date, { long: true })).toBe('7 September');
    expect(formatWeekdayDayMonth(date)).toBe('Mon 7 Sep');
    expect(formatMonthYear(date)).toBe('Sep 2026');
    expect(formatMonthYear(date, { long: true })).toBe('September 2026');
    expect(formatMonthShort(date)).toBe('Sep');
  });

  it('uses a zero-padded 24-hour clock', () => {
    expect(formatDateTime(date)).toBe('7 Sep 2026, 09:05');
    expect(formatDayMonthTime(new Date(2026, 9, 26, 14, 30))).toBe('26 Oct, 14:30');
  });

  it('reads calendar fields in UTC when asked', () => {
    const utcMidnight = '2026-01-01T00:00:00Z';
    expect(formatDateLong(utcMidnight, { utc: true })).toBe('1 January 2026');
    expect(formatMonthYear(utcMidnight, { utc: true })).toBe('Jan 2026');
  });

  it('drops the year only for dates in the current year', () => {
    const now = new Date(2026, 9, 26);
    expect(formatDateOmitCurrentYear(new Date(2026, 2, 3), now)).toBe('3 Mar');
    expect(formatDateOmitCurrentYear(new Date(2025, 2, 3), now)).toBe('3 Mar 2025');
  });
});
