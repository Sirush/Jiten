export function apiErrorMessage(error: unknown, fallback: string): string {
  const e = error as { statusCode?: number; data?: unknown } | null;
  const data = e?.data;
  if (typeof data === 'string' && data.trim()) return data;
  if (data && typeof data === 'object' && typeof (data as { message?: unknown }).message === 'string') return (data as { message: string }).message;
  return fallback;
}

export function apiStatusCode(error: unknown): number | undefined {
  return (error as { statusCode?: number } | null)?.statusCode;
}
