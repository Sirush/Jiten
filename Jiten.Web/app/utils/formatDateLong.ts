/** Always day, month, year ("26 October 2026"), whatever the browser locale. */
export function formatDateLong(date: string | number | Date): string {
  return new Date(date).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' });
}
