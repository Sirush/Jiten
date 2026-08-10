import { MediaType, MediaTypeGroup } from '~/types';

export function getMediaTypeText(mediaType: MediaType): string {
  switch (mediaType) {
    case MediaType.Anime:
      return 'Anime';
    case MediaType.Drama:
      return 'Drama';
    case MediaType.Movie:
      return 'Movie';
    case MediaType.Novel:
      return 'Novel';
    case MediaType.NonFiction:
      return 'Non-Fiction';
    case MediaType.VideoGame:
      return 'Video Game';
    case MediaType.VisualNovel:
      return 'Visual Novel';
    case MediaType.WebNovel:
      return 'Web Novel';
    case MediaType.Manga:
      return 'Manga';
    case MediaType.Audio:
      return 'Audio';
    default:
      return 'Unknown';
  }
}

// Media counted by volume in everyday speech: the unit total is the headline, whole works the sub-line.
const unitHeadlineTypes = new Set([MediaType.Novel, MediaType.Manga, MediaType.NonFiction, MediaType.WebNovel]);

export interface CompletedDisplay {
  value: number;
  label: string;
  sub: string | null;
}

function unitLabel(count: number, mediaType: MediaType | null): string {
  const plural = mediaType === null ? 'Entries' : getChildrenCountText(mediaType);
  return count === 1 ? (plural === 'Entries' ? 'Entry' : plural.replace(/s$/, '')) : plural;
}

/** Sub-line is null for visual novels (routes are not a tracked unit), not-yet-backfilled rows (unit count 0), or when it would repeat the headline. */
export function getCompletedDisplay(mediaType: MediaType | null, completedUnitCount: number, completedDeckCount: number): CompletedDisplay {
  const units = completedUnitCount || 0;

  if (mediaType !== null && unitHeadlineTypes.has(mediaType) && units > 0) {
    return {
      value: units,
      label: `${unitLabel(units, mediaType)} Completed`,
      sub: units === completedDeckCount
        ? null
        : `${completedDeckCount.toLocaleString()} ${completedDeckCount === 1 ? 'work' : 'works'} completed`,
    };
  }

  return {
    value: completedDeckCount,
    label: 'Completed',
    sub: mediaType !== MediaType.VisualNovel && units > completedDeckCount
      ? `${units.toLocaleString()} ${unitLabel(units, mediaType)}`
      : null,
  };
}

export function getChildrenCountText(mediaType: MediaType): string {
  switch (mediaType) {
    case MediaType.Anime:
      return 'Episodes';
    case MediaType.Drama:
      return 'Episodes';
    case MediaType.Movie:
      return 'Movies';
    case MediaType.Manga:
      return 'Volumes';
    case MediaType.Novel:
      return 'Volumes';
    case MediaType.NonFiction:
      return 'Volumes';
    case MediaType.VideoGame:
      return 'Entries';
    case MediaType.VisualNovel:
      return 'Routes';
    case MediaType.WebNovel:
      return 'Parts';
    case MediaType.Audio:
      return 'Entries';
    default:
      return 'Unknown';
  }
}

export function getMediaTypeGroupText(group: MediaTypeGroup): string {
  switch (group) {
    case MediaTypeGroup.Prose:
      return 'Prose';
    case MediaTypeGroup.VisualText:
      return 'Visual Text';
    case MediaTypeGroup.AudioVisual:
      return 'Audio Visual';
    case MediaTypeGroup.NonFiction:
      return 'Non-Fiction';
    default:
      return 'Unknown';
  }
}
