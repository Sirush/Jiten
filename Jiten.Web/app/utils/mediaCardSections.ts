export const MEDIA_CARD_SECTION_IDS = ['description', 'genres', 'tags', 'relations'] as const;

export type MediaCardSectionId = (typeof MEDIA_CARD_SECTION_IDS)[number];

export interface MediaCardSectionLayout {
  top: MediaCardSectionId[];
  bottom: MediaCardSectionId[];
}

export type MediaCardSectionZone = keyof MediaCardSectionLayout;

export const DEFAULT_MEDIA_CARD_SECTION_LAYOUT: MediaCardSectionLayout = {
  top: ['description'],
  bottom: ['genres', 'tags', 'relations'],
};

export const MEDIA_CARD_SECTION_LABELS: Record<MediaCardSectionId, string> = {
  description: 'Description',
  genres: 'Genres',
  tags: 'Tags',
  relations: 'Relations',
};

export function isMediaCardSectionLayout(value: unknown): value is MediaCardSectionLayout {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false;
  const { top, bottom, ...rest } = value as Record<string, unknown>;
  if (Object.keys(rest).length > 0 || !Array.isArray(top) || !Array.isArray(bottom)) return false;
  const all = [...top, ...bottom];
  return all.length === MEDIA_CARD_SECTION_IDS.length && MEDIA_CARD_SECTION_IDS.every((id) => all.includes(id));
}

export const copySectionLayout = (layout: MediaCardSectionLayout): MediaCardSectionLayout => ({ top: [...layout.top], bottom: [...layout.bottom] });
