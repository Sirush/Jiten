import { describe, expect, it } from 'vitest';
import { nearestTagWeight, searchTags, splitTagMatch, tagWeightText } from '~/utils/tags';

describe('nearestTagWeight', () => {
  it('snaps arbitrary percentages to the closest level', () => {
    expect([0, 33, 49, 50, 66, 82, 84, 100].map(nearestTagWeight)).toEqual([33, 33, 33, 66, 66, 66, 100, 100]);
  });
});

const tags = [
  { tagId: 1, name: 'Time Travel' },
  { tagId: 2, name: 'Female Protagonist' },
  { tagId: 3, name: 'Travel' },
  { tagId: 4, name: 'Protagonist' },
  { tagId: 5, name: 'Male Protagonist' },
  { tagId: 6, name: 'Pro Wrestling' },
  { tagId: 7, name: 'Isekai' },
];

describe('tagWeightText', () => {
  it('names exact levels and falls back to the percentage otherwise', () => {
    expect([33, 66, 100, 78].map(tagWeightText)).toEqual(['minor', 'moderate', 'major', '78%']);
  });
});

describe('searchTags', () => {
  it('lists every tag alphabetically for an empty query', () => {
    expect(searchTags(tags, '  ').map((t) => t.name)).toEqual([
      'Female Protagonist',
      'Isekai',
      'Male Protagonist',
      'Pro Wrestling',
      'Protagonist',
      'Time Travel',
      'Travel',
    ]);
  });

  it('ranks exact, prefix, word-start, then substring matches', () => {
    expect(searchTags(tags, 'protagonist').map((t) => t.name)).toEqual(['Protagonist', 'Female Protagonist', 'Male Protagonist']);
    expect(searchTags(tags, 'pro').map((t) => t.name)).toEqual(['Pro Wrestling', 'Protagonist', 'Female Protagonist', 'Male Protagonist']);
    expect(searchTags(tags, 'travel').map((t) => t.name)).toEqual(['Travel', 'Time Travel']);
  });

  it('matches mid-word substrings last and ignores case', () => {
    expect(searchTags(tags, 'SEK').map((t) => t.name)).toEqual(['Isekai']);
    expect(searchTags(tags, 'zzz')).toEqual([]);
  });
});

describe('splitTagMatch', () => {
  it('splits around the first case-insensitive match', () => {
    expect(splitTagMatch('Time Travel', 'tra')).toEqual({ before: 'Time ', match: 'Tra', after: 'vel' });
  });

  it('returns the whole name as before when nothing matches', () => {
    expect(splitTagMatch('Isekai', '')).toEqual({ before: 'Isekai', match: '', after: '' });
    expect(splitTagMatch('Isekai', 'xyz')).toEqual({ before: 'Isekai', match: '', after: '' });
  });
});
