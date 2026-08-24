export const DEFAULT_TTS_VOLUME = 1;

/// Both HTMLMediaElement.volume and SpeechSynthesisUtterance.volume only accept 0-1; anything else is rejected outright.
export function resolveTtsVolume(stored: unknown): number {
  if (typeof stored !== 'number' || !Number.isFinite(stored)) return DEFAULT_TTS_VOLUME;
  return Math.min(1, Math.max(0, stored));
}

export function isTtsMuted(stored: unknown): boolean {
  return resolveTtsVolume(stored) === 0;
}
