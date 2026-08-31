import { LinkType } from '~/types';

export function getLinkTypeText(linkType: LinkType): string {
  switch (linkType) {
    case LinkType.Web:
      return 'Website';
    case LinkType.Tmdb:
      return 'TMDB';
    case LinkType.Anilist:
      return 'Anilist';
    case LinkType.Mal:
      return 'MyAnimeList';
    case LinkType.GoogleBooks:
      return 'Google Books';
    case LinkType.Imdb:
      return 'IMDB';
    case LinkType.Vndb:
      return 'VNDB';
    case LinkType.Igdb:
      return 'IGDB';
    case LinkType.Syosetsu:
      return 'Syosetu';
    case LinkType.Bookmeter:
      return 'Bookmeter';
    case LinkType.Amazon:
      return 'Amazon';
    case LinkType.YouTube:
      return 'YouTube';

    default:
      return 'Unknown';
  }
}

const vndbReleasePath = /^\/r\d+/;

export function isVndbReleaseUrl(url: string | null | undefined): boolean {
  if (!url) return false;

  try {
    return vndbReleasePath.test(new URL(url).pathname);
  } catch {
    return false;
  }
}

export function getLinkLabel(link: { linkType: LinkType | number; url?: string | null }): string {
  const text = getLinkTypeText(link.linkType as LinkType);

  if (link.linkType !== LinkType.Vndb) return text;

  return isVndbReleaseUrl(link.url) ? `${text} (release)` : text;
}

const hostedDomains: Array<{ domains: string[]; type: LinkType }> = [
  { domains: ['vndb.org'], type: LinkType.Vndb },
  { domains: ['themoviedb.org'], type: LinkType.Tmdb },
  { domains: ['anilist.co'], type: LinkType.Anilist },
  { domains: ['myanimelist.net'], type: LinkType.Mal },
  { domains: ['imdb.com'], type: LinkType.Imdb },
  { domains: ['igdb.com'], type: LinkType.Igdb },
  { domains: ['syosetu.com'], type: LinkType.Syosetsu },
  { domains: ['bookmeter.com'], type: LinkType.Bookmeter },
  { domains: ['amzn.to', 'amzn.asia'], type: LinkType.Amazon },
  { domains: ['youtube.com', 'youtu.be', 'youtube-nocookie.com'], type: LinkType.YouTube },
];

const amazonHost = /(^|\.)amazon\.[a-z]{2,3}(\.[a-z]{2})?$/;

export function detectLinkTypeFromUrl(url: string): LinkType | null {
  let parsed: URL;
  try {
    parsed = new URL(url.trim());
  } catch {
    return null;
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;

  const host = parsed.hostname.toLowerCase();

  for (const { domains, type } of hostedDomains) {
    if (domains.some((domain) => host === domain || host.endsWith(`.${domain}`))) return type;
  }

  if (amazonHost.test(host)) return LinkType.Amazon;

  // Mirrors ExternalUrlParser.IsGoogleBooksHost: google.* is Books only on a /books/ path
  if (host.startsWith('books.google.')) return LinkType.GoogleBooks;
  if ((host.startsWith('google.') || host.startsWith('www.google.')) && parsed.pathname.toLowerCase().includes('/books/'))
    return LinkType.GoogleBooks;

  return LinkType.Web;
}
