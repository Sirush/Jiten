const MIN_COVERAGE = 50;

/** "1 in X words unknown" for a coverage percentage of running text; null where the ratio says nothing useful (under 50%, or nothing unknown). */
export function formatUnknownWordRatio(coverage: number): string | null {
  if (!Number.isFinite(coverage) || coverage < MIN_COVERAGE || coverage >= 100) return null;
  const every = parseFloat((100 / (100 - coverage)).toPrecision(2));
  return `1 in ${every.toLocaleString('en-US')} words unknown`;
}
