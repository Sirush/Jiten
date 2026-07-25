/** True when a failed $api call was rejected by the rate limiter, which is worth offering a retry for. */
export function isRateLimited(err: unknown): boolean {
  const e = err as { statusCode?: number; response?: { status?: number } };
  return (e?.statusCode ?? e?.response?.status) === 429;
}
