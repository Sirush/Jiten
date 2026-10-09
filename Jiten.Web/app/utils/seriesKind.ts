import { SeriesKind } from '~/types/series';

export function seriesKindLabel(kind: SeriesKind): string {
  return kind === SeriesKind.Setting ? 'Setting' : 'Series';
}
