import { getMediaTypeText } from './mediaTypeMapper';

export type ActiveNotice = { key: string; values: Record<string, string> };

export type NoticeCopy = { title: string; body: string; link?: { label: string; to: string } };

/** Copy per notice key from OneTimeNoticeRegistry in Jiten.Api; a key missing here is never rendered. */
export const ONE_TIME_NOTICE_COPY: Record<string, (values: Record<string, string>) => NoticeCopy> = {
  'study-order-follows-rank-source': (values) => {
    const source = values.mediaType ? getMediaTypeText(Number(values.mediaType)) : values.listName || 'your list';
    return {
      title: 'Decks in frequency order now use your rank source',
      body: `New cards from your media and word list decks now come in ${source} rank order instead of global rank.`,
      link: { label: 'Change rank source', to: '/settings/vocabulary' },
    };
  },
};
