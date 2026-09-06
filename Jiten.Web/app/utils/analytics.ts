export function trackEvent(name: string, data?: Record<string, string | number | boolean>): void {
  if (import.meta.server) return;
  try {
    beatEvent(name, data);
  } catch {}
}

export function lengthBucket(length: number): string {
  if (length < 20) return '<20';
  if (length < 200) return '<200';
  if (length < 2000) return '<2k';
  if (length < 20000) return '<20k';
  return '20k+';
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
