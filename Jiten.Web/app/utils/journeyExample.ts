import type { CoverageJourney, JourneyPoint, JourneyMilestone } from '~/types';

let cached: CoverageJourney | null = null;
let cachedAnchor: string | null = null;

// The locked state shows a fabricated curve rather than a blurred real one: the API never sends a
// non-subscriber their own series, so there is nothing real to blur.
const BUCKETS = 13;
const START = 34;
const END = 92;
// Growth arrives in bursts: a month of heavy study, then a near-flat one. The weights only scale each
// step's share of the climb, so the curve never drops and still lands exactly on END.
const BURST_WEIGHTS = [2.1, 0.1, 1.5, 0.5];

function easeOut(t: number): number {
  return 1 - Math.pow(1 - t, 2.2);
}

function buildCoverageSeries(): number[] {
  const deltas: number[] = [];
  for (let i = 1; i < BUCKETS; i++) {
    const step = easeOut(i / (BUCKETS - 1)) - easeOut((i - 1) / (BUCKETS - 1));
    deltas.push(step * BURST_WEIGHTS[(i - 1) % BURST_WEIGHTS.length]!);
  }
  const scale = (END - START) / deltas.reduce((sum, d) => sum + d, 0);
  const series = [START];
  for (const delta of deltas) series.push(series[series.length - 1]! + delta * scale);
  return series;
}

function isoMonth(monthsAgo: number): string {
  const now = new Date();
  const date = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - monthsAgo, 1));
  return date.toISOString().slice(0, 10);
}

/**
 * Built once per page load: every locked deck page shows the same fabricated curve. The cache is keyed
 * on the current month so a long-lived SSR process cannot hand a client stale dates to hydrate against.
 */
export function buildExampleJourney(): CoverageJourney {
  const anchor = isoMonth(0);
  if (cached && cachedAnchor === anchor) return cached;

  const points: JourneyPoint[] = [];
  const series = buildCoverageSeries();

  for (let i = 0; i < BUCKETS; i++) {
    const t = i / (BUCKETS - 1);
    const coverage = series[i]!;
    const unique = coverage * 0.62;
    points.push({
      date: isoMonth(BUCKETS - 1 - i),
      coverage: Math.round(coverage * 10) / 10,
      combinedCoverage: Math.min(100, Math.round((coverage + 4) * 10) / 10),
      uniqueCoverage: Math.round(unique * 10) / 10,
      combinedUniqueCoverage: Math.round((unique + 3) * 10) / 10,
      knownWords: Math.round(400 + 5200 * t),
      knownWordsCombined: Math.round(500 + 5600 * t),
    });
  }

  const milestones: JourneyMilestone[] = [];
  for (const threshold of [50, 80, 90]) {
    const point = points.find((p) => p.coverage >= threshold);
    if (point) milestones.push({ threshold, reachedAt: point.date, unique: false });
  }

  cached = {
    deckId: 0,
    granularity: 'monthly',
    points,
    milestones,
    startDate: points[0]!.date,
    startCoverage: points[0]!.coverage,
    currentCoverage: points[points.length - 1]!.coverage,
    startUniqueCoverage: points[0]!.uniqueCoverage,
    currentUniqueCoverage: points[points.length - 1]!.uniqueCoverage,
    hasEnoughHistory: true,
    asOf: null,
  };
  cachedAnchor = anchor;

  return cached;
}
