const NUMBER_LOCALE = 'en-US';
const defaultFormatter = new Intl.NumberFormat(NUMBER_LOCALE);

/** One fixed locale, so the server and every browser render the same digits and a number never changes after hydration. */
export function formatNumber(value: number, options?: Intl.NumberFormatOptions): string {
  return options ? new Intl.NumberFormat(NUMBER_LOCALE, options).format(value) : defaultFormatter.format(value);
}
