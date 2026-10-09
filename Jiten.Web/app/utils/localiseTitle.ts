import { TitleLanguage } from '~/types';

type Localisable = { originalTitle: string; romajiTitle?: string | null; englishTitle?: string | null };

export function localiseTitleWithLanguage(deck: Localisable, titleLanguage: TitleLanguage): string {
  if (titleLanguage === TitleLanguage.Original) {
    return deck.originalTitle;
  }

  if (titleLanguage === TitleLanguage.Romaji) {
    return deck.romajiTitle || deck.originalTitle;
  }

  if (titleLanguage === TitleLanguage.English) {
    return deck.englishTitle || deck.romajiTitle || deck.originalTitle;
  }

  return deck.originalTitle;
}

export function pickTitles(source: Localisable): { originalTitle: string; romajiTitle: string | null; englishTitle: string | null } {
  return { originalTitle: source.originalTitle, romajiTitle: source.romajiTitle ?? null, englishTitle: source.englishTitle ?? null };
}
