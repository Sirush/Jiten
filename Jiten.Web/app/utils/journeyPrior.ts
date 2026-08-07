import type { JourneyPoint, GrowthPoint } from '~/types';

/** Whether any of the series is carried by words the user declared known in bulk rather than studied. */
export function hasPriorKnowledge(points: (JourneyPoint | GrowthPoint)[]): boolean {
  return points.some((p) => (p.priorKnownWords ?? 0) > 0);
}
