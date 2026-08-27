import { describe, expect, it } from 'vitest';
import { getLinkLabel, getLinkTypeText } from '../app/utils/linkTypeMapper';
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

describe('getLinkTypeText', () => {
  it('stays a plain enum label', () => {
    expect(getLinkTypeText(LinkType.Vndb)).toBe('VNDB');
    expect(getLinkTypeText(99 as LinkType)).toBe('Unknown');
  });
});
