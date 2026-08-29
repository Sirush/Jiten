import type { Reading, ResolvedFrequencyRank } from '~/types';

/**
 * Single-number encoding for a rank source, so one Select and one popover can offer all three kinds:
 * 0 = the site-wide ranking, positive = a MediaType id, negative = a custom frequency list id.
 */
export function frequencySourceValue(resolved: ResolvedFrequencyRank | null | undefined): number {
  if (!resolved) return 0;
  if (resolved.source === 'list' && resolved.listId != null) return -resolved.listId;
  // A fallback rank still belongs to the media type the user chose, not to Global.
  return resolved.mediaType ?? 0;
}

/** The settings fields for an encoded source; 0 is the explicit "back to global" the server expects. */
export function frequencySourcePatch(value: number) {
  return {
    defaultFrequencyMediaType: value > 0 ? value : 0,
    defaultFrequencyListId: value < 0 ? -value : 0,
  };
}

/**
 * What a list row prints for its rank: the row's own fallback flag wins over the page-level
 * source label, so a global stand-in rank is never labelled with the requested media type.
 */
export function rowRankLabel(
  reading: Pick<Reading, 'frequencyRank' | 'frequencyRankSource' | 'isFrequencyFallback'>,
  requestedSourceLabel?: string
): { rank: string; source: string | null; hint: string | null } {
  const rank = reading.frequencyRank > 0 ? reading.frequencyRank.toLocaleString() : '\u2014';
  if (!requestedSourceLabel) return { rank, source: null, hint: null };
  if (reading.isFrequencyFallback) {
    return { rank, source: 'global', hint: `Not present in ${requestedSourceLabel} yet, so this is the global rank instead.` };
  }
  if (!reading.frequencyRankSource || reading.frequencyRankSource === 'global') return { rank, source: null, hint: null };
  return { rank, source: requestedSourceLabel, hint: null };
}
