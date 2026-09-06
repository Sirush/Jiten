/** "12:34" under an hour, "1h 05m" above; empty for a missing or zero length. */
export function formatRuntime(seconds: number | null | undefined): string {
  if (!seconds || seconds <= 0) return '';
  const total = Math.round(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const secs = total % 60;
  if (hours > 0) return `${hours}h ${minutes.toString().padStart(2, '0')}m`;
  return `${minutes}:${secs.toString().padStart(2, '0')}`;
}
