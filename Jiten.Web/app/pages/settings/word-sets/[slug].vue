<script setup lang="ts">
import type { WordSetDto, Word } from '~/types/types';
import { WordSetStateType } from '~/types';
import { autoLinkUrls } from '~/utils/autoLinkUrls';
import { debounce } from 'perfect-debounce';
import { parseStringArray, toBooleanOrNull } from '~/utils/queryParams';

definePageMeta({
  middleware: ['auth'],
});

const route = useRoute();
const router = useRouter();
const slug = route.params.slug as string;

const { fetchSubscriptions, subscriptions, subscribe, unsubscribe, isSubscribed, getSubscriptionState } = useWordSets();
const authStore = useAuthStore();
const toast = useToast();

const subscribingSetId = ref<number | null>(null);
const selectedState = ref<WordSetStateType>(WordSetStateType.Blacklisted);

const stateOptions = [
  { label: 'Blacklist', value: WordSetStateType.Blacklisted },
  { label: 'Mark as Mastered', value: WordSetStateType.Mastered },
];

const sortByOptions = ref([
  { label: 'Position', value: 'position' },
  { label: 'Global Frequency', value: 'globalFreq' },
]);

const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
const limit = computed(() => (route.query.limit ? Number(route.query.limit) : undefined));
const sortDescending = ref(route.query.sortOrder === '1');
const sortBy = ref(route.query.sortBy?.toString() || sortByOptions.value[0].value);
const { tiers: displayTiers, suspended, redundant, displayFilter, suspendedParam, redundantParam, query: displayQuery } = useVocabularyDisplayFilter();
const search = ref(route.query.search?.toString() || '');
const debouncedSearch = ref(search.value);

const includePos = ref<string[]>(parseStringArray(route.query.pos));
const excludePos = ref<string[]>(parseStringArray(route.query.excludePos));
const hideKanaOnly = ref(toBooleanOrNull(route.query.hideKanaOnly) ?? false);

watch(sortDescending, (newValue) => {
  router.replace({
    query: { ...route.query, sortOrder: newValue ? '1' : '0', offset: 0 },
  });
});

watch(sortBy, (newValue) => {
  router.replace({
    query: { ...route.query, sortBy: newValue, offset: 0 },
  });
});

const updateSearch = debounce((val: string) => {
  debouncedSearch.value = val;
  router.replace({ query: { ...route.query, search: val || undefined, offset: undefined } });
}, 300);
watch(search, updateSearch);

const debouncedIncludePos = ref([...includePos.value]);
const debouncedExcludePos = ref([...excludePos.value]);
const debouncedHideKanaOnly = ref(hideKanaOnly.value);

const updateAdvancedFilters = debounce(() => {
  debouncedIncludePos.value = [...includePos.value];
  debouncedExcludePos.value = [...excludePos.value];
  debouncedHideKanaOnly.value = hideKanaOnly.value;
  router.replace({
    query: {
      ...route.query,
      pos: includePos.value.length > 0 ? includePos.value.join(',') : undefined,
      excludePos: excludePos.value.length > 0 ? excludePos.value.join(',') : undefined,
      hideKanaOnly: hideKanaOnly.value ? 'true' : undefined,
      offset: 0,
    },
  });
}, 500);

watch([includePos, excludePos, hideKanaOnly], updateAdvancedFilters, { deep: true });

const { data: wordSet, status: wordSetStatus, error: wordSetError } = await useApiFetch<WordSetDto>(`word-sets/${slug}`);

const {
  data: response,
  status: vocabStatus,
  error: vocabError,
} = await useApiFetchPaginated<Word[]>(`word-sets/${slug}/vocabulary`, {
  query: {
    offset: offset,
    sortBy: sortBy,
    sortOrder: computed(() => sortDescending.value ? 1 : 0),
    ...displayQuery,
    search: debouncedSearch,
    pos: computed(() => debouncedIncludePos.value.length > 0 ? debouncedIncludePos.value.join(',') : undefined),
    excludePos: computed(() => debouncedExcludePos.value.length > 0 ? debouncedExcludePos.value.join(',') : undefined),
    hideKanaOnly: debouncedHideKanaOnly,
    limit: limit,
  },
  watch: [offset, sortBy, sortDescending, displayFilter, suspendedParam, redundantParam, debouncedSearch, debouncedIncludePos, debouncedExcludePos, debouncedHideKanaOnly, limit],
});

const { start, end, totalItems, previousLink, nextLink, currentPage, totalPages, pageLinkFor, pageSize } = usePagination(response);

const listContext = computed(() => ({
  label: wordSet.value?.name ?? 'Word set',
  sortLabel: sortByOptions.value.find((o) => o.value === sortBy.value)?.label,
  sortDescending: sortDescending.value,
  offset: offset.value,
  totalItems: totalItems.value,
  pageSize: pageSize.value,
}));

async function handleSubscribe(state: WordSetStateType) {
  if (!authStore.isAuthenticated) {
    navigateTo('/login');
    return;
  }

  if (!wordSet.value) return;

  subscribingSetId.value = wordSet.value.setId;
  const success = await subscribe(wordSet.value.setId, state);
  subscribingSetId.value = null;

  if (success) {
    await fetchSubscriptions();
    const stateLabel = state === WordSetStateType.Blacklisted ? 'blacklisted' : 'mastered';
    toast.add({
      severity: 'success',
      summary: 'Subscribed',
      detail: `${wordSet.value.name} words marked as ${stateLabel}`,
      life: 3000,
    });
  } else {
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail: 'Failed to subscribe to word set',
      life: 5000,
    });
  }
}

async function handleUnsubscribe() {
  if (!wordSet.value) return;

  subscribingSetId.value = wordSet.value.setId;
  const success = await unsubscribe(wordSet.value.setId);
  subscribingSetId.value = null;

  if (success) {
    await fetchSubscriptions();
    toast.add({
      severity: 'success',
      summary: 'Unsubscribed',
      detail: `Removed ${wordSet.value.name} subscription`,
      life: 3000,
    });
  } else {
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail: 'Failed to unsubscribe from word set',
      life: 5000,
    });
  }
}

function getStateLabel(state: WordSetStateType): string {
  return state === WordSetStateType.Blacklisted ? 'Blacklisted' : 'Mastered';
}

function getStateSeverity(state: WordSetStateType): string {
  return state === WordSetStateType.Blacklisted ? 'secondary' : 'success';
}

useHead(() => ({
  title: wordSet.value ? `${wordSet.value.name} - Word Sets - Jiten` : 'Word Set - Jiten',
}));

onMounted(async () => {
  if (authStore.isAuthenticated) {
    await fetchSubscriptions();
  }
});

watch(() => authStore.isAuthenticated, (isAuth) => {
  if (isAuth) {
    fetchSubscriptions();
  }
});
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <div v-if="wordSetStatus === 'pending'" class="flex justify-center items-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" />
    </div>

    <div v-else-if="wordSetError || !wordSet" class="text-center py-12">
      <h1 class="text-2xl font-bold mb-4">Word Set Not Found</h1>
      <NuxtLink to="/settings/word-sets">
        <Button label="Back to Word Sets" icon="pi pi-arrow-left" />
      </NuxtLink>
    </div>

    <template v-else>
      <!-- Header Section -->
      <Card class="mb-4">
        <template #title>
          <div class="flex items-center justify-between flex-wrap gap-2">
            <div class="flex items-center gap-2">
              <NuxtLink to="/settings/word-sets">
                <Button icon="pi pi-arrow-left" severity="secondary" text />
              </NuxtLink>
              <span>{{ wordSet.name }}</span>
            </div>
            <Tag
              v-if="isSubscribed(wordSet.setId)"
              :value="getStateLabel(getSubscriptionState(wordSet.setId)!)"
              :severity="getStateSeverity(getSubscriptionState(wordSet.setId)!)"
            />
          </div>
        </template>
        <template #subtitle>
          <span class="text-muted-color">
            {{ wordSet.wordCount.toLocaleString() }} words &middot; {{ wordSet.formCount.toLocaleString() }} forms
          </span>
        </template>
        <template #content>
          <p
            v-if="wordSet.description"
            class="text-sm mb-4"
            v-html="sanitiseHtml(autoLinkUrls(wordSet.description))"
          />
          <div class="flex gap-2 flex-wrap">
            <template v-if="!isSubscribed(wordSet.setId)">
              <Select
                v-model="selectedState"
                :options="stateOptions"
                optionLabel="label"
                optionValue="value"
              />
              <Button
                label="Subscribe"
                icon="pi pi-plus"
                :loading="subscribingSetId === wordSet.setId"
                @click="handleSubscribe(selectedState)"
              />
            </template>
            <template v-else>
              <Button
                label="Unsubscribe"
                icon="pi pi-times"
                severity="danger"
                outlined
                :loading="subscribingSetId === wordSet.setId"
                @click="handleUnsubscribe"
              />
            </template>
          </div>
        </template>
      </Card>

      <!-- Vocabulary Section -->
      <div class="flex flex-col gap-2">
        <VocabularyFilters
          v-model:sort-by="sortBy"
          v-model:sort-descending="sortDescending"
          v-model:display-tiers="displayTiers"
          v-model:suspended="suspended"
          v-model:redundant="redundant"
          v-model:search="search"
          v-model:include-pos="includePos"
          v-model:exclude-pos="excludePos"
          v-model:hide-kana-only="hideKanaOnly"
          :sort-by-options="sortByOptions"
          :show-display-filter="authStore.isAuthenticated"
        />

        <PaginationControls :previous-link="previousLink" :next-link="nextLink" :current-page="currentPage" :total-pages="totalPages" :page-link-for="pageLinkFor" :start="start" :end="end" :total-items="totalItems" item-label="words" :page-size="pageSize" :page-size-options="[25, 50, 100]" mobile-compact />

        <VocabularyList
          :words="response?.data ?? []"
          :status="vocabStatus"
          :error="vocabError"
          :list-context="listContext"
        />

        <PaginationControls :previous-link="previousLink" :next-link="nextLink" :current-page="currentPage" :total-pages="totalPages" :page-link-for="pageLinkFor" :start="start" :end="end" :total-items="totalItems" :show-summary="false" :scroll-to-top-on-navigate="true" />
      </div>
    </template>
  </div>
</template>
