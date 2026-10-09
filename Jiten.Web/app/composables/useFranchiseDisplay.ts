import { useJitenStore } from '~/stores/jitenStore';
import type { FranchiseCardSize, FranchiseLinkMode, FranchiseSeriesDensity } from '~/utils/franchiseLayout';

interface DisplayOption<T> {
  label: string;
  value: T;
}

export const franchiseLinkModeOptions: DisplayOption<FranchiseLinkMode>[] = [
  { label: 'Story', value: 'story' },
  { label: 'All', value: 'all' },
  { label: 'None', value: 'none' },
];

export const franchiseCardSizeOptions: DisplayOption<FranchiseCardSize>[] = [
  { label: 'Covers', value: 'covers' },
  { label: 'Compact', value: 'compact' },
];

export const franchiseDensityOptions: DisplayOption<FranchiseSeriesDensity>[] = [
  { label: 'Detailed', value: 'detailed' },
  { label: 'Compact', value: 'compact' },
  { label: 'Main entries', value: 'main' },
];

export function useFranchiseDisplay() {
  const store = useJitenStore();

  const linkMode = computed<FranchiseLinkMode>({
    get: () => (franchiseLinkModeOptions.some((o) => o.value === store.franchiseLinkMode) ? store.franchiseLinkMode : 'all'),
    set: (v) => (store.franchiseLinkMode = v),
  });
  const cardSize = computed<FranchiseCardSize>({
    get: () => (store.franchiseCardSize === 'compact' ? 'compact' : 'covers'),
    set: (v) => (store.franchiseCardSize = v),
  });
  const density = computed<FranchiseSeriesDensity>({
    get: () => (franchiseDensityOptions.some((o) => o.value === store.franchiseSeriesDensity) ? store.franchiseSeriesDensity : 'detailed'),
    set: (v) => (store.franchiseSeriesDensity = v),
  });

  return { linkMode, cardSize, density };
}
