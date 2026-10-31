export function apiErrorMessage(error: unknown, fallback: string): string {
  const e = error as { statusCode?: number; data?: unknown } | null;
  const data = e?.data;
  if (typeof data === 'string' && data.trim()) return data;
  if (data && typeof data === 'object') {
    const body = data as { message?: unknown; detail?: unknown; title?: unknown; errors?: unknown };
    if (typeof body.message === 'string') return body.message;
    if (typeof body.detail === 'string' && body.detail.trim()) return body.detail;
    if (body.errors && typeof body.title === 'string') return body.title;
  }
  return fallback;
}

export function apiStatusCode(error: unknown): number | undefined {
  return (error as { statusCode?: number } | null)?.statusCode;
}
