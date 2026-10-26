/** Month and weekday names for every date shown on the site; localising dates means swapping these tables, never reaching for the browser locale. */
export const MONTHS_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'] as const;
export const MONTHS_LONG = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
] as const;
export const WEEKDAYS_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;

export type DateInput = string | number | Date;

export interface DateFormatOptions {
  /** Read the calendar fields in UTC instead of the viewer's zone, for dates stored as bare UTC days. */
  utc?: boolean;
}

interface DateParts {
  year: number;
  month: number;
  day: number;
  weekday: number;
  hours: number;
  minutes: number;
}

function toParts(input: DateInput, options?: DateFormatOptions): DateParts {
  const date = input instanceof Date ? input : new Date(input);
  return options?.utc
    ? {
        year: date.getUTCFullYear(),
        month: date.getUTCMonth(),
        day: date.getUTCDate(),
        weekday: date.getUTCDay(),
        hours: date.getUTCHours(),
        minutes: date.getUTCMinutes(),
      }
    : {
        year: date.getFullYear(),
        month: date.getMonth(),
        day: date.getDate(),
        weekday: date.getDay(),
        hours: date.getHours(),
        minutes: date.getMinutes(),
      };
}

const pad2 = (value: number) => String(value).padStart(2, '0');

/** "26 October 2026" */
export function formatDateLong(date: DateInput, options?: DateFormatOptions): string {
  const p = toParts(date, options);
  return `${p.day} ${MONTHS_LONG[p.month]} ${p.year}`;
}

/** "26 Oct 2026" */
export function formatDateShort(date: DateInput, options?: DateFormatOptions): string {
  const p = toParts(date, options);
  return `${p.day} ${MONTHS_SHORT[p.month]} ${p.year}`;
}

/** "26 Oct", or "26 October" with `long`. */
export function formatDayMonth(date: DateInput, options?: DateFormatOptions & { long?: boolean }): string {
  const p = toParts(date, options);
  return `${p.day} ${(options?.long ? MONTHS_LONG : MONTHS_SHORT)[p.month]}`;
}

/** "26 Oct" this year, "26 Oct 2025" otherwise. */
export function formatDateOmitCurrentYear(date: DateInput, now: Date = new Date()): string {
  return toParts(date).year === now.getFullYear() ? formatDayMonth(date) : formatDateShort(date);
}

/** "Mon 26 Oct" */
export function formatWeekdayDayMonth(date: DateInput, options?: DateFormatOptions): string {
  const p = toParts(date, options);
  return `${WEEKDAYS_SHORT[p.weekday]} ${p.day} ${MONTHS_SHORT[p.month]}`;
}

/** "Oct 2026", or "October 2026" with `long`. */
export function formatMonthYear(date: DateInput, options?: DateFormatOptions & { long?: boolean }): string {
  const p = toParts(date, options);
  return `${(options?.long ? MONTHS_LONG : MONTHS_SHORT)[p.month]} ${p.year}`;
}

/** "Oct" */
export function formatMonthShort(date: DateInput, options?: DateFormatOptions): string {
  return MONTHS_SHORT[toParts(date, options).month];
}

/** "26 Oct 2026, 14:05" on a 24-hour clock. */
export function formatDateTime(date: DateInput, options?: DateFormatOptions): string {
  const p = toParts(date, options);
  return `${formatDateShort(date, options)}, ${pad2(p.hours)}:${pad2(p.minutes)}`;
}

/** "26 Oct, 14:05" on a 24-hour clock. */
export function formatDayMonthTime(date: DateInput, options?: DateFormatOptions): string {
  const p = toParts(date, options);
  return `${formatDayMonth(date, options)}, ${pad2(p.hours)}:${pad2(p.minutes)}`;
}
