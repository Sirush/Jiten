import { parseStringArray } from '~/utils/queryParams';

export type VocabularyModifierMode = 'show' | 'hide' | 'only';

export const vocabularyTierOptions = [
  { label: 'Unknown', value: 'unknown', hint: 'Not in your vocabulary' },
  { label: 'Learning', value: 'learning', hint: 'In your vocabulary, not reviewed yet' },
  { label: 'Young', value: 'young', hint: 'Interval under 21 days' },
  { label: 'Mature', value: 'mature', hint: 'Interval of 21 days or more' },
  { label: 'Mastered', value: 'mastered', hint: 'Marked as always known' },
  { label: 'Blacklisted', value: 'blacklisted', hint: 'Never scheduled for review' },
];

const TIER_VALUES = vocabularyTierOptions.map((o) => o.value);
const KNOWN_TIERS = ['learning', 'young', 'mature', 'mastered', 'blacklisted'];

const parseTiers = (raw: unknown): string[] => {
  const tiers: string[] = [];
  for (const token of parseStringArray(raw)) {
    const value = token.toLowerCase();
    if (value === 'all') return [];
    // Links made before the checkbox list carried a single value; "known" was every tier but Unknown.
    const expanded = value === 'known' ? KNOWN_TIERS : [value === 'new' ? 'unknown' : value];
    for (const tier of expanded) {
      if (TIER_VALUES.includes(tier) && !tiers.includes(tier)) tiers.push(tier);
    }
  }
  return tiers;
};

const parseMode = (raw: unknown): VocabularyModifierMode => {
  const value = Array.isArray(raw) ? raw[0] : raw;
  return value === 'hide' || value === 'only' ? value : 'show';
};

// State for the vocabulary Display control, kept in the URL and shaped into API query params.
export function useVocabularyDisplayFilter() {
  const route = useRoute();
  const router = useRouter();

  const tiers = ref<string[]>(parseTiers(route.query.display));
  const suspended = ref<VocabularyModifierMode>(parseMode(route.query.suspended));
  const redundant = ref<VocabularyModifierMode>(parseMode(route.query.redundant));

  watch([tiers, suspended, redundant], () => {
    router.replace({
      query: {
        ...route.query,
        display: tiers.value.length > 0 ? tiers.value.join(',') : undefined,
        suspended: suspended.value === 'show' ? undefined : suspended.value,
        redundant: redundant.value === 'show' ? undefined : redundant.value,
        offset: 0,
      },
    });
  }, { deep: true });

  const displayFilter = computed(() => (tiers.value.length > 0 ? tiers.value.join(',') : 'all'));
  const suspendedParam = computed(() => (suspended.value === 'show' ? undefined : suspended.value));
  const redundantParam = computed(() => (redundant.value === 'show' ? undefined : redundant.value));

  return {
    tiers,
    suspended,
    redundant,
    displayFilter,
    suspendedParam,
    redundantParam,
    query: { displayFilter, suspended: suspendedParam, redundant: redundantParam },
  };
}
