import type { MediaType } from '~/types';

export interface GroupStudyFilter {
  groupMediaTypes: MediaType[];
  groupExcludedDeckIds: number[];
}

/** Every available type ticked (or none) sends no narrowing, so media types the group gains later are studied too. */
export function buildGroupStudyFilter(
  selectedTypes: MediaType[],
  availableTypes: MediaType[],
  excludedDeckIds: number[],
  memberDeckIds: number[]
): GroupStudyFilter {
  const available = new Set(availableTypes);
  const selected = [...new Set(selectedTypes)].filter((t) => available.has(t));
  const narrows = selected.length > 0 && selected.length < available.size;
  const members = new Set(memberDeckIds);

  return {
    groupMediaTypes: narrows ? selected.sort((a, b) => a - b) : [],
    groupExcludedDeckIds: [...new Set(excludedDeckIds)].filter((id) => members.has(id)).sort((a, b) => a - b),
  };
}

/** Ticked types for the dialog: a stored null or empty list means every type the group has. */
export function groupTypesForForm(stored: MediaType[] | null | undefined, availableTypes: MediaType[]): MediaType[] {
  return stored && stored.length > 0 ? stored.filter((t) => availableTypes.includes(t)) : [...availableTypes];
}
