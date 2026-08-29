import { describe, expect, it } from 'vitest';
import { buildSameAs } from '../app/composables/useDeckSchema';
import { LinkType } from '../app/types/enums';

const link = (linkType: LinkType, url: string) => ({ linkType, url });

describe('buildSameAs', () => {
  it('keeps the visual novel page and drops the release page, in either order', () => {
    const v = link(LinkType.Vndb, 'https://vndb.org/v405');
    const r = link(LinkType.Vndb, 'https://vndb.org/r43621');
    expect(buildSameAs([v, r])).toEqual(['https://vndb.org/v405']);
    expect(buildSameAs([r, v])).toEqual(['https://vndb.org/v405']);
  });

  it('emits nothing for a deck whose only VNDB link is a release', () => {
    expect(buildSameAs([link(LinkType.Vndb, 'https://vndb.org/r43621')])).toEqual([]);
  });

  it('keeps a VNDB release listing page, which is still the visual novel', () => {
    expect(buildSameAs([link(LinkType.Vndb, 'https://vndb.org/v12345/releases')])).toEqual(['https://vndb.org/v12345/releases']);
  });

  it('never applies the release rule to other providers', () => {
    const links = [
      link(LinkType.Mal, 'https://myanimelist.net/r12345'),
      link(LinkType.Anilist, 'https://anilist.co/anime/1'),
      link(LinkType.Bookmeter, 'https://bookmeter.com/books/548199'),
    ];
    expect(buildSameAs(links)).toEqual(links.map((l) => l.url));
  });

  it('still excludes generic and commercial links', () => {
    expect(buildSameAs([link(LinkType.Web, 'https://example.com'), link(LinkType.Amazon, 'https://amazon.co.jp/dp/B00ABC1234')])).toEqual([]);
  });

  it('tolerates a malformed VNDB URL rather than throwing during SSR', () => {
    expect(buildSameAs([link(LinkType.Vndb, 'not a url')])).toEqual(['not a url']);
  });

  it('handles an empty or absent link list', () => {
    expect(buildSameAs([])).toEqual([]);
    expect(buildSameAs(undefined)).toEqual([]);
  });
});
