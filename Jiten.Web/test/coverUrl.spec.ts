import { describe, expect, it } from 'vitest';
import { coverUrl } from '~/utils/coverImage';

describe('coverUrl', () => {
  const cdnCover = 'https://cdn.jiten.moe/283/cover.jpg';

  it('falls back to the placeholder for missing art', () => {
    expect(coverUrl(null, 'sm')).toBe('/img/nocover.jpg');
    expect(coverUrl('nocover.jpg', 'sm')).toBe('/img/nocover.jpg');
  });

  it('returns the stored original without a size', () => {
    expect(coverUrl(cdnCover)).toBe(cdnCover);
  });

  it('asks the CDN optimizer for the preset width', () => {
    expect(coverUrl(cdnCover, 'xs')).toBe(`${cdnCover}?width=96`);
    expect(coverUrl(cdnCover, 'lg')).toBe(`${cdnCover}?width=640`);
  });

  it('leaves covers on other hosts untouched', () => {
    expect(coverUrl('https://jiten-dev.b-cdn.net/283/cover.jpg', 'md')).toBe('https://jiten-dev.b-cdn.net/283/cover.jpg');
    expect(coverUrl('/static/283/cover.jpg', 'md')).toBe('/static/283/cover.jpg');
  });
});
