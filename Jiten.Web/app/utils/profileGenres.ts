import type { ProfileGenreShare } from '~/types';
import { getGenreText } from '~/utils/genreMapper';
import { formatApprox } from '~/utils/siteStats';

/** Swatches for a profile's top genres by rank, shared by the overview card and the Stats tab so a genre keeps its colour. */
const GENRE_SWATCHES = ['bg-blue-500', 'bg-amber-600', 'bg-teal-600', 'bg-rose-500', 'bg-violet-500'];

export function genreRows(genres: ProfileGenreShare[]) {
  return genres.map((g, i) => ({
    key: String(g.genre),
    label: getGenreText(g.genre),
    swatch: GENRE_SWATCHES[i] ?? 'bg-surface-400 dark:bg-surface-500',
    share: g.share,
    percent: Math.round(g.share * 100),
    tooltip: `About ${formatApprox(g.characters)} characters`,
  }));
}
