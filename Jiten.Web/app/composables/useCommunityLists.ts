import { CommunityListStatus, type Genre } from '~/types';
import type {
  CommunityListEdit,
  CommunityListFacets,
  CommunityListItem,
  CommunityListMembership,
  CommunityListQuota,
  CommunityListSummary,
  PaginatedResponse,
} from '~/types/types';

export type CommunityListSort = 'top' | 'popular' | 'newest' | 'updated' | 'random';
export type CommunityListItemSort = 'curator' | 'coverage';

export interface CommunityListFilterQuery {
  q?: string;
  genres?: number[];
  excludeGenres?: number[];
  tags?: number[];
  excludeTags?: number[];
}

export interface CommunityListDetailsInput {
  name: string;
  description: string | null;
  status: CommunityListStatus;
  genres: Genre[];
  tagIds: number[];
}

const csv = (ids: number[] | undefined) => (ids && ids.length ? ids.join(',') : undefined);

export const COMMUNITY_LIST_ITEM_PAGE_SIZE = 50;
export const COMMUNITY_LIST_MAX_NAME = 100;
export const COMMUNITY_LIST_MAX_DESCRIPTION = 1000;
export const COMMUNITY_LIST_MAX_NOTE = 300;
export const COMMUNITY_LIST_MAX_REPORT = 500;

/** Content a list's name, description and notes must not contain; shared by the creation rules and the report dialog so both stay in step. */
export const COMMUNITY_LIST_FORBIDDEN_CONTENT = [
  'hate speech, harassment or personal attacks',
  'inappropriate sexual or shocking content',
  'spam, advertising or links to pirate websites',
  "someone's personal information",
];

export const COMMUNITY_LIST_STATUS_TEXT: Record<CommunityListStatus, string> = {
  [CommunityListStatus.Draft]: 'Draft',
  [CommunityListStatus.Public]: 'Public',
  [CommunityListStatus.Unlisted]: 'Unlisted',
};

export const unlistConfirmation = (accept: () => unknown) => ({
  header: 'Unlist this list?',
  message: 'It will be hidden from everyone except its owner and the moderators, and its open reports will be resolved.',
  rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
  acceptProps: { label: 'Unlist', severity: 'danger' },
  accept,
});

export const communityListItemAnchor = (position: number) => `item-${position}`;

/** Canonical list URL; the slug is cosmetic and the page loads by id. */
export function communityListPath(list: { listId: number; slug: string }): string {
  return list.slug ? `/lists/${list.listId}/${list.slug}` : `/lists/${list.listId}`;
}

/** Titles picked before their list existed; the new-list page adds them once the list is created. */
export const usePendingListAdd = () => useState<number[]>('community-list-pending-add', () => []);

export function useCommunityLists() {
  const { $api } = useNuxtApp();

  const filterQuery = (query: CommunityListFilterQuery) => ({
    q: query.q?.trim() || undefined,
    genres: csv(query.genres),
    excludeGenres: csv(query.excludeGenres),
    tags: csv(query.tags),
    excludeTags: csv(query.excludeTags),
  });

  return {
    facets: (query: CommunityListFilterQuery) => $api<CommunityListFacets>('lists/facets', { query: filterQuery(query) }),
    items: (listId: number, offset: number, sort?: CommunityListItemSort, q?: string, limit = COMMUNITY_LIST_ITEM_PAGE_SIZE) =>
      $api<PaginatedResponse<CommunityListItem[]>>(`lists/${listId}/items`, {
        query: { offset, limit, sort: sort === 'coverage' ? sort : undefined, q: q?.trim() || undefined },
      }),
    mine: () => $api<{ lists: CommunityListSummary[]; quota: CommunityListQuota }>('lists/mine'),
    quota: () => $api<CommunityListQuota>('lists/quota'),
    favourited: (offset: number) => $api<PaginatedResponse<CommunityListSummary[]>>('lists/favourited', { query: { offset } }),
    getForEdit: (listId: number) => $api<CommunityListEdit>(`lists/${listId}/edit`),
    create: (name: string, description: string | null) => $api<{ listId: number; slug: string }>('lists', { method: 'POST', body: { name, description } }),
    updateDetails: (listId: number, details: CommunityListDetailsInput) =>
      $api<{ slug: string; status: CommunityListStatus }>(`lists/${listId}`, { method: 'PUT', body: details }),
    replaceItems: (listId: number, items: { deckId: number; note: string | null }[]) =>
      $api<{ itemCount: number }>(`lists/${listId}/items`, { method: 'PUT', body: { items } }),
    membership: (deckIds: number[]) =>
      $api<{ lists: CommunityListMembership[]; quota: CommunityListQuota }>('lists/mine/membership', { method: 'POST', body: { deckIds } }),
    addItems: (listId: number, items: { deckId: number; note: string | null }[], position?: number) =>
      $api<{ added: number; alreadyInList: number; itemCount: number }>(`lists/${listId}/items`, { method: 'POST', body: { items, position } }),
    removeItem: (listId: number, deckId: number) =>
      $api<{ position: number; note: string | null; itemCount: number }>(`lists/${listId}/items/${deckId}`, { method: 'DELETE' }),
    remove: (listId: number) => $api(`lists/${listId}`, { method: 'DELETE' }),
    favourite: (listId: number) => $api<{ favourited: boolean; favouriteCount: number }>(`lists/${listId}/favourite`, { method: 'POST' }),
    unfavourite: (listId: number) => $api<{ favourited: boolean; favouriteCount: number }>(`lists/${listId}/favourite`, { method: 'DELETE' }),
    report: (listId: number, reason: string) => $api(`lists/${listId}/report`, { method: 'POST', body: { reason } }),
  };
}
