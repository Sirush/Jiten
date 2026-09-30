import { describe, expect, it } from 'vitest';
import { DEFAULT_MEDIA_CARD_SECTION_LAYOUT, isMediaCardSectionLayout } from '../app/utils/mediaCardSections';

describe('isMediaCardSectionLayout', () => {
  it('accepts every section placed once across the two zones, either zone empty', () => {
    expect(isMediaCardSectionLayout(DEFAULT_MEDIA_CARD_SECTION_LAYOUT)).toBe(true);
    expect(isMediaCardSectionLayout({ top: ['tags', 'description', 'relations', 'genres'], bottom: [] })).toBe(true);
  });

  it('rejects a missing, repeated or unknown section', () => {
    expect(isMediaCardSectionLayout({ top: ['description'], bottom: ['genres', 'tags'] })).toBe(false);
    expect(isMediaCardSectionLayout({ top: ['description', 'tags'], bottom: ['genres', 'tags'] })).toBe(false);
    expect(isMediaCardSectionLayout({ top: ['description', 'bogus'], bottom: ['genres', 'tags', 'relations'] })).toBe(false);
  });

  it('rejects other shapes', () => {
    expect(isMediaCardSectionLayout(['description', 'genres', 'tags', 'relations'])).toBe(false);
    expect(isMediaCardSectionLayout({ top: ['description'], bottom: ['genres', 'tags', 'relations'], side: [] })).toBe(false);
    expect(isMediaCardSectionLayout(null)).toBe(false);
  });
});
