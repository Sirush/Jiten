import { describe, expect, it } from 'vitest';
import { detectLinkTypeFromUrl, getLinkLabel, getLinkTypeText, isVndbReleaseUrl } from '../app/utils/linkTypeMapper';
import { LinkType } from '../app/types/enums';

const vndb = (url: string) => getLinkLabel({ linkType: LinkType.Vndb, url });

describe('getLinkLabel', () => {
  it('marks VNDB release pages', () => {
    expect(vndb('https://vndb.org/r12345')).toBe('VNDB (release)');
    expect(vndb('https://vndb.org/r12345/')).toBe('VNDB (release)');
    expect(vndb('https://VNDB.ORG/r12345')).toBe('VNDB (release)');
    expect(vndb('https://vndb.org/r12345.4')).toBe('VNDB (release)');
  });

  it('leaves visual novel pages alone', () => {
    expect(vndb('https://vndb.org/v12345')).toBe('VNDB');
    expect(vndb('https://vndb.org/v12345/releases')).toBe('VNDB');
  });

  it('falls back to the plain label on unusable URLs', () => {
    expect(vndb('')).toBe('VNDB');
    expect(vndb('not a url')).toBe('VNDB');
    expect(vndb('/r12345')).toBe('VNDB');
    expect(getLinkLabel({ linkType: LinkType.Vndb })).toBe('VNDB');
    expect(getLinkLabel({ linkType: LinkType.Vndb, url: null })).toBe('VNDB');
  });

  it('never qualifies other link types', () => {
    expect(getLinkLabel({ linkType: LinkType.Mal, url: 'https://myanimelist.net/r12345' })).toBe('MyAnimeList');
    expect(getLinkLabel({ linkType: LinkType.Web, url: 'https://example.com/r1' })).toBe('Website');
  });

  it('sorts the visual novel link before its release link', () => {
    const links = [
      { linkType: LinkType.Vndb, url: 'https://vndb.org/r12345' },
      { linkType: LinkType.Vndb, url: 'https://vndb.org/v12345' },
    ];
    const sorted = [...links].sort((a, b) => getLinkLabel(a).localeCompare(getLinkLabel(b)));
    expect(sorted.map(getLinkLabel)).toEqual(['VNDB', 'VNDB (release)']);
  });
});

describe('isVndbReleaseUrl', () => {
  it('matches release pages including sub-releases and odd casing', () => {
    expect(isVndbReleaseUrl('https://vndb.org/r12345')).toBe(true);
    expect(isVndbReleaseUrl('https://vndb.org/r12345/')).toBe(true);
    expect(isVndbReleaseUrl('https://VNDB.ORG/r12345')).toBe(true);
    expect(isVndbReleaseUrl('https://vndb.org/r12345.4')).toBe(true);
  });

  it('rejects visual novel pages', () => {
    expect(isVndbReleaseUrl('https://vndb.org/v12345')).toBe(false);
    expect(isVndbReleaseUrl('https://vndb.org/v12345/releases')).toBe(false);
  });

  it('rejects unusable input instead of throwing', () => {
    expect(isVndbReleaseUrl('')).toBe(false);
    expect(isVndbReleaseUrl('not a url')).toBe(false);
    expect(isVndbReleaseUrl('/r12345')).toBe(false);
    expect(isVndbReleaseUrl(null)).toBe(false);
    expect(isVndbReleaseUrl(undefined)).toBe(false);
  });
});

describe('getLinkTypeText', () => {
  it('stays a plain enum label', () => {
    expect(getLinkTypeText(LinkType.Vndb)).toBe('VNDB');
    expect(getLinkTypeText(99 as LinkType)).toBe('Unknown');
  });
});

describe('detectLinkTypeFromUrl', () => {
  it('detects each provider from its host', () => {
    expect(detectLinkTypeFromUrl('https://vndb.org/v12345')).toBe(LinkType.Vndb);
    expect(detectLinkTypeFromUrl('https://www.themoviedb.org/tv/12345-slug')).toBe(LinkType.Tmdb);
    expect(detectLinkTypeFromUrl('https://anilist.co/anime/1')).toBe(LinkType.Anilist);
    expect(detectLinkTypeFromUrl('https://myanimelist.net/anime/1')).toBe(LinkType.Mal);
    expect(detectLinkTypeFromUrl('https://www.imdb.com/title/tt0111161/')).toBe(LinkType.Imdb);
    expect(detectLinkTypeFromUrl('https://www.igdb.com/games/clannad')).toBe(LinkType.Igdb);
    expect(detectLinkTypeFromUrl('https://ncode.syosetu.com/n2267be/')).toBe(LinkType.Syosetsu);
    expect(detectLinkTypeFromUrl('https://bookmeter.com/books/548199')).toBe(LinkType.Bookmeter);
  });

  it('detects Amazon across TLDs and short links', () => {
    expect(detectLinkTypeFromUrl('https://www.amazon.co.jp/dp/B00ABC1234')).toBe(LinkType.Amazon);
    expect(detectLinkTypeFromUrl('https://amazon.com/dp/B00ABC1234')).toBe(LinkType.Amazon);
    expect(detectLinkTypeFromUrl('https://amazon.de/dp/B00ABC1234')).toBe(LinkType.Amazon);
    expect(detectLinkTypeFromUrl('https://amzn.to/3xYz')).toBe(LinkType.Amazon);
    expect(detectLinkTypeFromUrl('https://amzn.asia/d/abc')).toBe(LinkType.Amazon);
    expect(detectLinkTypeFromUrl('https://notamazon.com/dp/B00ABC1234')).toBe(LinkType.Web);
  });

  it('detects Google Books only on book hosts or /books/ paths', () => {
    expect(detectLinkTypeFromUrl('https://books.google.com/books?id=abc')).toBe(LinkType.GoogleBooks);
    expect(detectLinkTypeFromUrl('https://books.google.co.jp/books?id=abc')).toBe(LinkType.GoogleBooks);
    expect(detectLinkTypeFromUrl('https://www.google.com/books/edition/_/abc')).toBe(LinkType.GoogleBooks);
    expect(detectLinkTypeFromUrl('https://www.google.com/search?q=x')).toBe(LinkType.Web);
  });

  it('is case-insensitive on the host and tolerates surrounding whitespace', () => {
    expect(detectLinkTypeFromUrl('  HTTPS://VNDB.ORG/v11 ')).toBe(LinkType.Vndb);
  });

  it('falls back to Web for valid URLs on unknown hosts', () => {
    expect(detectLinkTypeFromUrl('https://example.com/whatever')).toBe(LinkType.Web);
  });

  it('returns null for unusable input so the current selection is kept', () => {
    expect(detectLinkTypeFromUrl('')).toBeNull();
    expect(detectLinkTypeFromUrl('vndb.org/v123')).toBeNull();
    expect(detectLinkTypeFromUrl('not a url')).toBeNull();
    expect(detectLinkTypeFromUrl('ftp://vndb.org/v123')).toBeNull();
  });
});
