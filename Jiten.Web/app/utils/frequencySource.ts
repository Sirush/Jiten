import type { ResolvedFrequencyRank } from '~/types';

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
