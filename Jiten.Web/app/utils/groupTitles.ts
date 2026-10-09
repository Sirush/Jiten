import type { MediaGroupTitles } from '~/types/mediaGroup';

export const GROUP_TITLE_MAX_LENGTH = 200;

/** Form state for a franchise or series; romaji and English stay optional. */
export interface GroupTitlesDraft {
  originalTitle: string;
  romajiTitle: string;
  englishTitle: string;
}

export function titlesDraft(titles?: MediaGroupTitles | null): GroupTitlesDraft {
  return { originalTitle: titles?.originalTitle ?? '', romajiTitle: titles?.romajiTitle ?? '', englishTitle: titles?.englishTitle ?? '' };
}

/** Trimmed, with blank romaji and English titles sent as null, as the API stores them. */
export function titlesFromDraft(draft: GroupTitlesDraft): { originalTitle: string; romajiTitle: string | null; englishTitle: string | null } {
  return {
    originalTitle: draft.originalTitle.trim(),
    romajiTitle: draft.romajiTitle.trim() || null,
    englishTitle: draft.englishTitle.trim() || null,
  };
}

export function sameTitles(a: MediaGroupTitles, b: MediaGroupTitles): boolean {
  return a.originalTitle === b.originalTitle && (a.romajiTitle || null) === (b.romajiTitle || null) && (a.englishTitle || null) === (b.englishTitle || null);
}
