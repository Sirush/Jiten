type RelativeTimeStyle = 'long' | 'short';

interface RelativeUnit {
  seconds: number;
  long: string;
  short: string;
  shortPlural: string;
  previous?: string;
  next?: string;
}

const UNITS: RelativeUnit[] = [
  { seconds: 365 * 24 * 3600, long: 'year', short: 'yr', shortPlural: 'yr', previous: 'last', next: 'next' },
  { seconds: 30 * 24 * 3600, long: 'month', short: 'mo', shortPlural: 'mo', previous: 'last', next: 'next' },
  { seconds: 7 * 24 * 3600, long: 'week', short: 'wk', shortPlural: 'wk', previous: 'last', next: 'next' },
  { seconds: 24 * 3600, long: 'day', short: 'day', shortPlural: 'days' },
  { seconds: 3600, long: 'hour', short: 'hr', shortPlural: 'hr' },
  { seconds: 60, long: 'minute', short: 'min', shortPlural: 'min' },
];

/** English only: browser-locale output breaks the site fonts and mixes languages in an English interface. */
export function formatRelativeTime(value: string | Date, now: Date = new Date(), style: RelativeTimeStyle = 'long'): string {
  const date = typeof value === 'string' ? new Date(value) : value;
  const seconds = Math.round((now.getTime() - date.getTime()) / 1000);
  const unit = UNITS.find((u) => Math.abs(seconds) >= u.seconds);
  if (!unit) return 'just now';

  const count = Math.abs(Math.round(seconds / unit.seconds));
  const isPast = seconds > 0;
  const singular = style === 'long' ? unit.long : unit.short;

  if (count === 1) {
    if (unit.long === 'day') return isPast ? 'yesterday' : 'tomorrow';
    if (unit.previous && unit.next) return `${isPast ? unit.previous : unit.next} ${singular}`;
  }

  const plural = style === 'long' ? `${unit.long}s` : unit.shortPlural;
  const label = `${count} ${count === 1 ? singular : plural}`;
  return isPast ? `${label} ago` : `in ${label}`;
}
