import { describe, expect, it } from 'vitest';
import { MediaType } from '../app/types/enums';
import { buildGroupStudyFilter, groupTypesForForm } from '../app/utils/mediaGroupStudyDeck';

describe('buildGroupStudyFilter', () => {
  const available = [MediaType.Anime, MediaType.Novel, MediaType.VideoGame];

  it('sends no media types when every available type is ticked', () => {
    expect(buildGroupStudyFilter([MediaType.VideoGame, MediaType.Anime, MediaType.Novel], available, [], []).groupMediaTypes).toEqual([]);
  });

  it('sends no media types when none is ticked', () => {
    expect(buildGroupStudyFilter([], available, [], []).groupMediaTypes).toEqual([]);
  });

  it('sends a sorted subset and drops types the group does not have', () => {
    expect(buildGroupStudyFilter([MediaType.Novel, MediaType.Anime, MediaType.Manga], available, [], []).groupMediaTypes).toEqual([
      MediaType.Anime,
      MediaType.Novel,
    ]);
  });

  it('keeps only excluded decks that are still members, deduplicated and sorted', () => {
    expect(buildGroupStudyFilter([], available, [9, 3, 3, 42], [3, 9, 10]).groupExcludedDeckIds).toEqual([3, 9]);
  });
});

describe('groupTypesForForm', () => {
  const available = [MediaType.Anime, MediaType.Novel];

  it('ticks every available type for a stored null or empty list', () => {
    expect(groupTypesForForm(null, available)).toEqual(available);
    expect(groupTypesForForm([], available)).toEqual(available);
  });

  it('ticks the stored types the group still has', () => {
    expect(groupTypesForForm([MediaType.Novel, MediaType.Manga], available)).toEqual([MediaType.Novel]);
  });
});
