import { describe, expect, it } from 'vitest';
import { getMediaTypeFromSlug, getMediaTypeSlug } from '../app/utils/mediaTypeMapper';
import { MediaType } from '../app/types/enums';

const allTypes = Object.values(MediaType).filter((v): v is MediaType => typeof v === 'number');

describe('media type slugs', () => {
  it('round-trips every media type', () => {
    for (const type of allTypes) {
      expect(getMediaTypeFromSlug(getMediaTypeSlug(type))).toBe(type);
    }
  });

  it('slugs are unique and URL-safe', () => {
    const slugs = allTypes.map(getMediaTypeSlug);
    expect(new Set(slugs).size).toBe(slugs.length);
    for (const slug of slugs) {
      expect(slug).toMatch(/^[a-z0-9-]+$/);
    }
  });

  it('returns null for unknown slugs', () => {
    expect(getMediaTypeFromSlug('podcasts')).toBeNull();
    expect(getMediaTypeFromSlug('1')).toBeNull();
  });
});
