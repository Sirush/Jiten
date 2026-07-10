// Mirrors LeechHelper.cs: a card is an active leech when its lapse count has reached the
// user's leech threshold and it hasn't recovered since (current stability below the FSRS
// mature threshold). Derived on the fly, so changing the threshold retroactively updates
// which cards are flagged.
export const MATURE_STABILITY_DAYS = 21;

export function isLeechCard(lapses: number | undefined, stability: number | null | undefined, leechThreshold: number): boolean {
  return leechThreshold > 0 && (lapses ?? 0) >= leechThreshold && (stability ?? 0) < MATURE_STABILITY_DAYS;
}
