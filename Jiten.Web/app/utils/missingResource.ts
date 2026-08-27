/**
 * True only when the API definitively says the resource does not exist: a 404, or the 200-with-null
 * payload some endpoints return instead. Timeouts and 5xx return false on purpose — a transient API
 * failure during SSR must not become a 404 page, which would deindex a working URL.
 */
export function isMissingResource(error: unknown, data: unknown): boolean {
  const status =
    (error as { status?: number; statusCode?: number } | null | undefined)?.status ?? (error as { statusCode?: number } | null | undefined)?.statusCode;

  if (status === 404) return true;
  return !error && data == null;
}
