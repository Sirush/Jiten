import type { Genre } from '~/types/enums';

export interface SuggestionSource {
  genres: Genre[];
  tags: { tagId: number; name: string; percentage: number }[];
}

const SUGGESTION_SHARE = 0.4;
const MIN_ITEMS_FOR_SUGGESTIONS = 3;
const MAX_SUGGESTIONS = 3;

function pool(items: SuggestionSource[]): SuggestionSource[] {
  return items.filter((i) => i.genres.length || i.tags.length);
}

export function suggestGenres(items: SuggestionSource[], picked: Genre[]): { genre: Genre; share: number }[] {
  const sources = pool(items);
  if (sources.length < MIN_ITEMS_FOR_SUGGESTIONS) return [];
  const counts = new Map<Genre, number>();
  for (const item of sources) for (const genre of item.genres) counts.set(genre, (counts.get(genre) ?? 0) + 1);
  return [...counts.entries()]
    .map(([genre, count]) => ({ genre, share: count / sources.length }))
    .filter((s) => s.share >= SUGGESTION_SHARE && !picked.includes(s.genre))
    .sort((a, b) => b.share - a.share || a.genre - b.genre)
    .slice(0, MAX_SUGGESTIONS);
}

export function suggestTags(items: SuggestionSource[], picked: number[]): { tagId: number; name: string; share: number }[] {
  const sources = pool(items);
  if (sources.length < MIN_ITEMS_FOR_SUGGESTIONS) return [];
  const weights = new Map<number, { name: string; weight: number }>();
  for (const item of sources)
    for (const tag of item.tags) {
      const entry = weights.get(tag.tagId) ?? { name: tag.name, weight: 0 };
      entry.weight += tag.percentage / 100;
      weights.set(tag.tagId, entry);
    }
  return [...weights.entries()]
    .map(([tagId, entry]) => ({ tagId, name: entry.name, share: entry.weight / sources.length }))
    .filter((s) => s.share >= SUGGESTION_SHARE && !picked.includes(s.tagId))
    .sort((a, b) => b.share - a.share || a.name.localeCompare(b.name))
    .slice(0, MAX_SUGGESTIONS);
}
