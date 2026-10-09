import { type MediaGroupTitles, type StudyDeckDto, StudyDeckType } from '~/types';
import { mediaGroupKindWord, mediaGroupLabel, mediaGroupLink, mediaGroupName } from '~/utils/mediaGroup';

export interface StudyDeckPresentation {
  /** Group decks read "Franchise: X". */
  title: string;
  /** The bare name, for file names and form headings. */
  name: string;
  link: string | null;
}

export function studyDeckPresentation(deck: StudyDeckDto, localise: (title: MediaGroupTitles) => string): StudyDeckPresentation {
  if (deck.deckType === StudyDeckType.MediaDeck) {
    const title = localise({ originalTitle: deck.title, romajiTitle: deck.romajiTitle, englishTitle: deck.englishTitle });
    return { title, name: title, link: deck.deckId ? `/decks/media/${deck.deckId}/detail` : null };
  }
  if (deck.deckType === StudyDeckType.MediaGroup) {
    return {
      title: mediaGroupLabel(deck, localise),
      name: mediaGroupName(deck, localise) ?? (deck.title || mediaGroupKindWord(deck.groupKind)),
      link: mediaGroupLink(deck),
    };
  }
  return { title: deck.name, name: deck.name, link: null };
}
