import { describe, expect, it } from 'vitest';
import { Genre } from '../app/types/enums';
import { suggestGenres, suggestTags, type SuggestionSource } from '../app/utils/communityListSuggestions';

const item = (genres: Genre[], tags: [number, string, number][] = []): SuggestionSource => ({
  genres,
  tags: tags.map(([tagId, name, percentage]) => ({ tagId, name, percentage })),
});

describe('suggestGenres', () => {
  it('suggests genres carried by at least 40% of titles, most common first', () => {
    const items = [
      item([Genre.SliceOfLife, Genre.Comedy]),
      item([Genre.SliceOfLife]),
      item([Genre.SliceOfLife, Genre.Comedy]),
      item([Genre.Drama]),
      item([Genre.SliceOfLife]),
    ];
    expect(suggestGenres(items, [])).toEqual([
      { genre: Genre.SliceOfLife, share: 0.8 },
      { genre: Genre.Comedy, share: 0.4 },
    ]);
  });

  it('skips genres already picked', () => {
    const items = [item([Genre.Action]), item([Genre.Action]), item([Genre.Action])];
    expect(suggestGenres(items, [Genre.Action])).toEqual([]);
  });

  it('needs three titles with metadata, ignoring titles that have none', () => {
    const items = [item([Genre.Action]), item([Genre.Action]), item([]), item([])];
    expect(suggestGenres(items, [])).toEqual([]);
  });
});

describe('suggestTags', () => {
  it('weights tags by how strongly each title carries them', () => {
    const items = [
      item(
        [Genre.Comedy],
        [
          [1, 'School', 100],
          [2, 'Cooking', 20],
        ]
      ),
      item(
        [Genre.Comedy],
        [
          [1, 'School', 80],
          [2, 'Cooking', 20],
        ]
      ),
      item([Genre.Comedy], [[2, 'Cooking', 20]]),
    ];
    expect(suggestTags(items, [])).toEqual([{ tagId: 1, name: 'School', share: 0.6 }]);
  });
});
