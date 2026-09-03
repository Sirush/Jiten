import { Genre, MediaType, SortOrder } from '~/types/enums';
import { deckSortLabels, deckSortMeta } from '~/utils/deckSorting';
import { getGenreText } from '~/utils/genreMapper';
import { buildRangeChips, MEDIA_RANGE_SPECS, type MediaRangeKey } from '~/utils/mediaFilterRanges';
import { capturePresetQuery } from '~/utils/mediaFilterPresets';
import { getMediaTypePluralText } from '~/utils/mediaTypeMapper';
import type { RangeBounds } from '~/utils/rangeFilters';

export type MediaListMeta = { title: string; summary: string; description: string };

export const MEDIA_LIST_TITLE_MAX = 70;
export const MEDIA_LIST_SUMMARY_MAX = 170;
export const MEDIA_LIST_DESCRIPTION_MAX = 200;
const TITLE_SEARCH_MAX = 40;
const CLOSER = 'Difficulty ratings, vocabulary lists and free Anki decks for every title.';

const isEnumValue = (values: object, value: number) => Object.values(values).includes(value);

const parseMediaType = (raw: string | undefined): MediaType | null => {
  const value = Number(raw);
  return raw !== undefined && Number.isInteger(value) && isEnumValue(MediaType, value) ? (value as MediaType) : null;
};

const parseGenres = (raw: string | undefined): string[] =>
  (raw ?? '')
    .split(',')
    .map((part) => Number(part.trim()))
    .filter((value) => Number.isInteger(value) && isEnumValue(Genre, value))
    .map((value) => getGenreText(value as Genre));

const parseBound = (raw: string | undefined): number | null => {
  if (raw === undefined || raw === '') return null;
  const value = Number(raw);
  return Number.isFinite(value) ? value : null;
};

const clip = (text: string, max: number) => (text.length <= max ? text : `${text.slice(0, max - 3).trimEnd()}...`);

const joinList = (items: string[]) => (items.length <= 1 ? items.join('') : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`);

const sortPhrase = (query: Record<string, string>): { label: string; direction: string } | null => {
  const key = query.sortBy;
  if (!key || !(key in deckSortLabels) || !(key in deckSortMeta)) return null;
  const meta = deckSortMeta[key]!;
  const order = query.sortOrder === undefined ? meta.default : Number(query.sortOrder);
  const direction = order === SortOrder.Descending ? meta.desc : order === SortOrder.Ascending ? meta.asc : null;
  return direction === null ? null : { label: deckSortLabels[key]!, direction };
};

/** Coverage ranges depend on the viewer's account, so a recipient never sees the same list. */
const sharedRangeChips = (query: Record<string, string>): string[] => {
  const ranges = Object.fromEntries(
    MEDIA_RANGE_SPECS.map((spec) => [
      spec.key,
      spec.requiresAuth ? { min: null, max: null } : { min: parseBound(query[`${spec.key}Min`]), max: parseBound(query[`${spec.key}Max`]) },
    ])
  ) as Record<MediaRangeKey, RangeBounds>;
  return buildRangeChips(ranges).map((chip) => chip.label.toLowerCase());
};

/** Derives share-card text from the URL alone; returns null when nothing a recipient can see is filtered. */
export function buildMediaListMeta(source: Record<string, unknown>): MediaListMeta | null {
  const query = capturePresetQuery(source);

  const mediaType = parseMediaType(query.mediaType);
  const sort = sortPhrase(query);
  const includeGenres = parseGenres(query.genres);
  const excludeGenres = parseGenres(query.excludeGenres);
  const ranges = sharedRangeChips(query);
  const titleSearch = (query.title ?? '').trim().slice(0, TITLE_SEARCH_MAX);
  const excludeSequels = query.excludeSequels === 'true';

  if (mediaType === null && sort === null && !includeGenres.length && !excludeGenres.length && !ranges.length && !titleSearch && !excludeSequels) {
    return null;
  }

  const plural = mediaType === null ? 'Media' : getMediaTypePluralText(mediaType);
  const subject = `Japanese ${plural}`;
  const title = clip(sort ? `${subject} by ${sort.label}` : subject, MEDIA_LIST_TITLE_MAX);

  const clauses: string[] = [];
  if (titleSearch) clauses.push(`matching "${titleSearch}"`);
  if (sort) clauses.push(`ranked by ${sort.label.toLowerCase()}, ${sort.direction.toLowerCase()}`);
  if (includeGenres.length) clauses.push(`in ${joinList(includeGenres)}`);
  if (excludeGenres.length) clauses.push(`excluding ${joinList(excludeGenres)}`);
  if (excludeSequels) clauses.push('without sequels');
  const rangeClause = ranges.length ? `with ${joinList(ranges)}` : null;

  const sentence = (parts: string[]) => `${subject.charAt(0)}${subject.slice(1).toLowerCase()}${parts.length ? ` ${parts.join(', ')}` : ''}.`;

  const full = rangeClause ? [...clauses, rangeClause] : clauses;
  const summary = [sentence(full), sentence(clauses)].find((text) => text.length <= MEDIA_LIST_SUMMARY_MAX) ?? clip(sentence(clauses), MEDIA_LIST_SUMMARY_MAX);
  const withCloser = `${summary} ${CLOSER}`;
  const description = withCloser.length <= MEDIA_LIST_DESCRIPTION_MAX ? withCloser : summary;

  return { title, summary, description };
}
