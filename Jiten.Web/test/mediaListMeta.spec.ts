import { describe, expect, it } from 'vitest';
import { buildMediaListMeta, MEDIA_LIST_DESCRIPTION_MAX, MEDIA_LIST_SUMMARY_MAX, MEDIA_LIST_TITLE_MAX } from '../app/utils/mediaListMeta';
import { deckSortLabels } from '../app/utils/deckSorting';
import { MediaType } from '../app/types/enums';

const CLOSER = 'Difficulty ratings, vocabulary lists and free Anki decks for every title.';
const EXAMPLE = { mediaType: '7', offset: '0', sortBy: 'difficulty', sortOrder: '0', excludeGenres: '18' };

describe('media list share meta', () => {
  it('describes the example link', () => {
    expect(buildMediaListMeta(EXAMPLE)).toEqual({
      title: 'Japanese Visual Novels by Difficulty',
      summary: 'Japanese visual novels ranked by difficulty, easiest first, excluding Adult Only.',
      description: `Japanese visual novels ranked by difficulty, easiest first, excluding Adult Only. ${CLOSER}`,
    });
  });

  it('names the media type alone', () => {
    expect(buildMediaListMeta({ mediaType: '9' })).toEqual({
      title: 'Japanese Manga',
      summary: 'Japanese manga.',
      description: `Japanese manga. ${CLOSER}`,
    });
  });

  it('keeps every media type and sort key combination inside the image title cap', () => {
    const types = Object.values(MediaType).filter((value): value is MediaType => typeof value === 'number');
    for (const mediaType of types) {
      for (const sortBy of Object.keys(deckSortLabels)) {
        const title = buildMediaListMeta({ mediaType: String(mediaType), sortBy })!.title;
        expect(title.length).toBeLessThanOrEqual(MEDIA_LIST_TITLE_MAX);
        expect(title).not.toContain('...');
      }
    }
  });

  it('words the sort direction both ways and falls back to the key default', () => {
    expect(buildMediaListMeta({ sortBy: 'difficulty', sortOrder: '1' })?.description).toContain('ranked by difficulty, hardest first');
    expect(buildMediaListMeta({ sortBy: 'difficulty', sortOrder: '0' })?.description).toContain('ranked by difficulty, easiest first');
    expect(buildMediaListMeta({ sortBy: 'releaseDate' })?.description).toContain('ranked by release date, newest first');
    expect(buildMediaListMeta({ sortBy: 'charCount' })?.title).toBe('Japanese Media by Character Count');
  });

  it('lists included and excluded genres', () => {
    const meta = buildMediaListMeta({ mediaType: '1', genres: '6,12', excludeGenres: '5,18' });
    expect(meta?.description).toContain('in Fantasy and Romance, excluding Ecchi and Adult Only');
  });

  it('reflects a numeric range with the chip wording', () => {
    const meta = buildMediaListMeta({ mediaType: '4', difficultyMin: '2', difficultyMax: '3.5', charCountMax: '500000' });
    expect(meta?.description).toBe(`Japanese novels with characters up to 500,000 and difficulty 2.0 - 3.5. ${CLOSER}`);
  });

  it('mentions a title search and the sequel exclusion', () => {
    const meta = buildMediaListMeta({ title: ' steins ', excludeSequels: 'true' });
    expect(meta?.description).toBe(`Japanese media matching "steins", without sequels. ${CLOSER}`);
  });

  it('returns null for personal-only keys and an empty query', () => {
    expect(buildMediaListMeta({})).toBeNull();
    expect(buildMediaListMeta({ offset: '20' })).toBeNull();
    expect(buildMediaListMeta({ status: 'completed', favourite: 'true', coverageMin: '80', uTotalCoverageMax: '90', tags: '3', excludeTags: '4' })).toBeNull();
  });

  it('omits personal keys from an otherwise shareable query', () => {
    const meta = buildMediaListMeta({ mediaType: '7', coverageMin: '80', status: 'completed', favourite: 'true', tags: '3' });
    expect(meta?.description).toBe(`Japanese visual novels. ${CLOSER}`);
  });

  it('never prints Unknown or crashes on malformed values', () => {
    expect(buildMediaListMeta({ mediaType: '99', sortBy: 'nonsense', sortOrder: 'x', excludeGenres: 'abc', genres: '0,999', difficultyMin: 'NaN' })).toBeNull();
    const partial = buildMediaListMeta({ mediaType: 'abc', sortBy: 'difficulty', sortOrder: 'sideways', excludeGenres: 'abc,18' });
    expect(partial?.title).toBe('Japanese Media');
    expect(partial?.description).toBe(`Japanese media excluding Adult Only. ${CLOSER}`);
    expect(buildMediaListMeta({ mediaType: ['7', '9'], sortBy: ['difficulty'] })?.title).toBe('Japanese Visual Novels by Difficulty');
  });

  it('stays under the length cap with every filter key set', () => {
    const everything = {
      mediaType: '7',
      title: 'a very long search phrase that goes on and on and on',
      sortBy: 'difficulty',
      sortOrder: '0',
      status: 'completed',
      charCountMin: '10000',
      charCountMax: '5000000',
      difficultyMin: '1',
      difficultyMax: '4.5',
      releaseYearMin: '1995',
      releaseYearMax: '2020',
      uniqueKanjiMin: '500',
      uniqueKanjiMax: '2500',
      subdeckCountMin: '1',
      subdeckCountMax: '50',
      extRatingMin: '60',
      extRatingMax: '95',
      speechSpeedMin: '100',
      speechSpeedMax: '400',
      speechDurationMin: '5',
      speechDurationMax: '80',
      coverageMin: '10',
      coverageMax: '90',
      uniqueCoverageMin: '10',
      uniqueCoverageMax: '90',
      totalCoverageMin: '10',
      totalCoverageMax: '90',
      uTotalCoverageMin: '10',
      uTotalCoverageMax: '90',
      genres: '1,2,3,4,5,6,7,8',
      excludeGenres: '9,10,11,12,13,14,15,16,17,18',
      tags: '1,2,3',
      excludeTags: '4,5',
      excludeSequels: 'true',
      favourite: 'true',
    };
    const meta = buildMediaListMeta(everything);
    expect(meta).not.toBeNull();
    expect(meta!.title.length).toBeLessThanOrEqual(MEDIA_LIST_TITLE_MAX);
    expect(meta!.summary.length).toBeLessThanOrEqual(MEDIA_LIST_SUMMARY_MAX);
    expect(meta!.description.length).toBeLessThanOrEqual(MEDIA_LIST_DESCRIPTION_MAX);
    expect(meta!.description).not.toContain('Unknown');
    expect(meta!.description.startsWith(meta!.summary)).toBe(true);
  });
});
