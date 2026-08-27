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

    default:
      return 'Unknown';
  }
}

const vndbReleasePath = /^\/r\d+/;

export function getLinkLabel(link: { linkType: LinkType | number; url?: string | null }): string {
  const text = getLinkTypeText(link.linkType as LinkType);

  if (link.linkType !== LinkType.Vndb || !link.url) return text;

  try {
    return vndbReleasePath.test(new URL(link.url).pathname) ? `${text} (release)` : text;
  } catch {
    return text;
  }
}
