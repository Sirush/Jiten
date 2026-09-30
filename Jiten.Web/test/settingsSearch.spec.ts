import { describe, expect, it } from 'vitest';
import { matchesSettingsSearch } from '../app/composables/useSettingsSearch';

describe('matchesSettingsSearch', () => {
  it('matches everything on an empty query', () => {
    expect(matchesSettingsSearch('  ', ['Theme'])).toBe(true);
  });

  it('needs every term, in any order and any case', () => {
    expect(matchesSettingsSearch('SIZE word', ['Word size', undefined])).toBe(true);
    expect(matchesSettingsSearch('word colour', ['Word size'])).toBe(false);
  });

  it('ignores curly quotes and full-width letters', () => {
    expect(matchesSettingsSearch('always reduce', ['“Always reduce” stops animations'])).toBe(true);
    expect(matchesSettingsSearch('ｒｏｍａｊｉ', ['romaji'])).toBe(true);
  });
});
