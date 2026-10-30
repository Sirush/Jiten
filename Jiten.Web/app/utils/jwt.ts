/** Unverified claims, for client-side hints only; null when the token is missing or malformed. */
export function decodeJwtPayload(token: string | null | undefined): Record<string, unknown> | null {
  if (!token) return null;
  try {
    const payload = token.split('.')[1]!.replace(/-/g, '+').replace(/_/g, '/');
    const claims = JSON.parse(atob(payload));
    return claims && typeof claims === 'object' ? claims : null;
  } catch {
    return null;
  }
}
