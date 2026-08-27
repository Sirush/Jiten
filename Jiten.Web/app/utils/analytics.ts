export function trackEvent(name: string, data?: Record<string, string | number | boolean>): void {
  if (import.meta.server) return;
  try {
    umTrackEvent(name, data);
  } catch {}
}

const ACTIVATION_KEY = 'jiten.activated';

// Fires first_activation once per browser, on the first meaningful product action.
export function trackActivation(action: 'review' | 'deck_download'): void {
  if (import.meta.server) return;
  try {
    if (localStorage.getItem(ACTIVATION_KEY)) return;
    localStorage.setItem(ACTIVATION_KEY, '1');
  } catch {
    return;
  }
  trackEvent('first_activation', { action });
}
