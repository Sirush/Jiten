import type { Ref } from 'vue';
import { type Deck, MediaType, LinkType } from '~/types';
import { getMediaTypeText } from '~/utils/mediaTypeMapper';
import { getGenreText } from '~/utils/genreMapper';
import { getDifficultyName } from '~/utils/difficultyColours';

// Link types that are authoritative references about the work itself (good `sameAs` targets).
// Excludes generic Web links and commercial Amazon listings.
const SAMEAS_LINK_TYPES = new Set<LinkType>([
  LinkType.Vndb,
  LinkType.Tmdb,
  LinkType.Anilist,
  LinkType.Mal,
  LinkType.GoogleBooks,
  LinkType.Imdb,
  LinkType.Igdb,
  LinkType.Syosetsu,
  LinkType.Bookmeter,
]);

function toDateOnly(value: Date | string | undefined): string | undefined {
  if (!value) return undefined;
  const d = new Date(value);
  if (Number.isNaN(d.getTime()) || d.getFullYear() <= 1) return undefined;
  return d.toISOString().slice(0, 10);
}

// Synthesized description for decks lacking a metadata one — used for the JSON-LD `description`
// (metadata, not visible body text) so every deck has a unique entity description for search engines.
function synthDescription(d: Deck): string {
  const type = getMediaTypeText(d.mediaType).toLowerCase();
  const counts = d.characterCount
    ? ` with ${d.characterCount.toLocaleString()} characters and ${d.uniqueWordCount.toLocaleString()} unique words`
    : '';
  const difficulty = d.difficulty >= 0 ? ` Difficulty: ${getDifficultyName(d.difficulty)}.` : '';
  return `Vocabulary list, difficulty analysis and a downloadable Anki deck for ${d.originalTitle}, a Japanese ${type}${counts}.${difficulty}`;
}

/**
 * Emits per-deck schema.org JSON-LD (CreativeWork-family node + BreadcrumbList + WebPage).
 * SSR by default. Intentionally omits aggregateRating: the on-page external rating is sourced
 * from third-party databases and the page's vote count is difficulty votes, not rating votes.
 */
export function useDeckSchema(
  deck: Ref<Deck | undefined>,
  pageUrl: Ref<string>,
  parentDeck?: Ref<Deck | null | undefined>,
) {
  useSchemaOrg(
    computed(() => {
      const d = deck.value;
      if (!d) return [];

      const name = d.originalTitle || '';
      const alternateName = [...new Set([d.englishTitle, d.romajiTitle, ...(d.aliases ?? [])].filter(Boolean) as string[])].filter((n) => n !== name);

      const sameAs = (d.links ?? []).filter((l) => SAMEAS_LINK_TYPES.has(l.linkType)).map((l) => l.url);

      const image = d.coverName && d.coverName !== 'nocover.jpg' ? d.coverName : undefined;

      const datePublished = toDateOnly(d.releaseDate);

      const additionalProperty = [
        d.difficulty >= 0 ? { '@type': 'PropertyValue', name: 'Difficulty (0-5)', value: d.difficulty } : null,
        d.characterCount ? { '@type': 'PropertyValue', name: 'Character count', value: d.characterCount } : null,
        d.uniqueWordCount ? { '@type': 'PropertyValue', name: 'Unique words', value: d.uniqueWordCount } : null,
        d.uniqueKanjiCount ? { '@type': 'PropertyValue', name: 'Unique kanji', value: d.uniqueKanjiCount } : null,
      ].filter(Boolean);

      // Loosely typed: a dynamic schema.org bag fed to several defineX builders with differing shapes.
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const common: any = {
        name,
        ...(alternateName.length ? { alternateName } : {}),
        description: d.description || synthDescription(d),
        ...(image ? { image } : {}),
        ...(datePublished ? { datePublished } : {}),
        ...(d.genres?.length ? { genre: d.genres.map(getGenreText) } : {}),
        inLanguage: 'ja',
        ...(sameAs.length ? { sameAs } : {}),
        ...(additionalProperty.length ? { additionalProperty } : {}),
      };

      const crumbs = [
        { name: 'Home', item: '/' },
        { name: getMediaTypeText(d.mediaType), item: `/decks/media/list/${d.mediaType}` },
      ];
      const parent = parentDeck?.value;
      if (parent) {
        crumbs.push({ name: parent.originalTitle || getMediaTypeText(parent.mediaType), item: `/decks/media/${parent.deckId}/detail` });
      }
      crumbs.push({ name: name || getMediaTypeText(d.mediaType), item: pageUrl.value });

      const breadcrumb = defineBreadcrumb({ itemListElement: crumbs });

      let mediaNode;
      switch (d.mediaType) {
        case MediaType.Novel:
        case MediaType.WebNovel:
        case MediaType.NonFiction:
        case MediaType.Manga:
          mediaNode = defineBook(common);
          break;
        case MediaType.Movie:
          mediaNode = defineMovie(common);
          break;
        case MediaType.Anime:
        case MediaType.Drama:
          // @unhead/schema-org's Vue build doesn't export defineTVSeries; emit a raw node.
          mediaNode = { '@type': 'TVSeries', ...common };
          break;
        case MediaType.VideoGame:
        case MediaType.VisualNovel:
          // No defineVideoGame helper exists; emit a raw co-typed node for rich-result eligibility.
          mediaNode = {
            '@type': ['VideoGame', 'SoftwareApplication'],
            ...common,
            applicationCategory: 'GameApplication',
          };
          break;
        default:
          mediaNode = { '@type': 'CreativeWork', ...common };
      }

      return [defineWebPage(), breadcrumb, mediaNode];
    })
  );
}
