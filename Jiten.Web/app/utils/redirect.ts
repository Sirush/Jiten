/** Accepts only same-site absolute paths; anything else (external URLs, protocol-relative) resolves to null. */
export function safeRedirectPath(raw: unknown): string | null {
  const value = Array.isArray(raw) ? raw[0] : raw;
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') ? value : null;
}
