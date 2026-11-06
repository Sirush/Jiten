import { formatDateOmitCurrentYear } from '~/utils/dateFormat';

export type FsrsParametersSource = 'default' | 'optimised' | 'auto' | 'edited' | 'legacy';

const reviewsText = (count: number) => `${count.toLocaleString('en-US')} ${count === 1 ? 'review' : 'reviews'}`;

const SOURCE_TEXT: Record<FsrsParametersSource, { label: string; line: string; fitted?: boolean }> = {
  auto: { label: 'Automatic update', line: 'Updated automatically', fitted: true },
  optimised: { label: 'Optimised', line: 'Optimised', fitted: true },
  edited: { label: 'Edited by you', line: 'Edited by you' },
  legacy: { label: 'Custom', line: 'Custom parameters' },
  default: { label: 'Default', line: 'Default parameters' },
};

export function fsrsProvenanceLine(source: FsrsParametersSource, fittedAtUtc: string | null, fittedReviewCount: number, now: Date = new Date()): string {
  const text = SOURCE_TEXT[source] ?? SOURCE_TEXT.default;
  if (!text.fitted) return text.line;
  const on = fittedAtUtc ? ` on ${formatDateOmitCurrentYear(fittedAtUtc, now)}` : '';
  const from = fittedReviewCount > 0 ? ` from ${reviewsText(fittedReviewCount)}` : '';
  return `${text.line}${on}${from}`;
}

export function fsrsSourceLabel(source: FsrsParametersSource): string {
  return (SOURCE_TEXT[source] ?? SOURCE_TEXT.default).label;
}

/** "Fits your reviews 3.2% better", from the losses an automatic update records; null when there is nothing to compare. */
export function fsrsLossChangeText(loss: number | null, previousLoss: number | null): string | null {
  if (loss == null || previousLoss == null || previousLoss <= 0) return null;
  const change = ((previousLoss - loss) / previousLoss) * 100;
  const rounded = Math.round(Math.abs(change) * 10) / 10;
  if (rounded === 0) return 'Fits your reviews as well';
  return change > 0 ? `Fits your reviews ${rounded}% better` : `Fits your reviews ${rounded}% worse`;
}

export function fsrsHistoryDetails(entry: { reviewCount: number; loss: number | null; previousLoss: number | null }): string[] {
  const details = entry.reviewCount > 0 ? [reviewsText(entry.reviewCount)] : [];
  const lossChange = fsrsLossChangeText(entry.loss, entry.previousLoss);
  return lossChange ? [...details, lossChange] : details;
}
