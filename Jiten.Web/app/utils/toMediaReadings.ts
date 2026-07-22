import type { Reading } from '~/types/types';

export function toMediaReadings(readings: Reading[] | undefined | null): { text: string; readingIndex: number }[] {
  return (readings ?? []).map((r) => ({ text: r.text, readingIndex: r.readingIndex }));
}
