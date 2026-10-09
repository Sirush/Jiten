<script setup lang="ts">
  import Button from 'primevue/button';
  import Message from 'primevue/message';
  import Paginator from 'primevue/paginator';
  import ProgressSpinner from 'primevue/progressspinner';
  import Select from 'primevue/select';
  import { debounce } from 'perfect-debounce';
  import { FranchiseSuggestionScope, MediaType, type FranchiseSuggestion, type FranchiseSuggestionDeck, type PaginatedResponse } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { getLinkTypeText } from '~/utils/linkTypeMapper';
  import { releaseYearOf } from '~/utils/franchiseLayout';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';

  const PAGE_SIZE = 20;
  const { $api } = useNuxtApp();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();

  const query = ref('');
  const mediaType = ref<MediaType | null>(null);
  const scope = ref<FranchiseSuggestionScope>(FranchiseSuggestionScope.All);

  const mediaTypeOptions = [
    { value: null, label: 'Any media type' },
    ...Object.values(MediaType)
      .filter((v): v is MediaType => typeof v === 'number' && v !== MediaType.YouTube)
      .map((v) => ({ value: v, label: getMediaTypeText(v) })),
  ];
  const scopeOptions = [
    { value: FranchiseSuggestionScope.All, label: 'All matches' },
    { value: FranchiseSuggestionScope.Franchise, label: 'Next to a franchise' },
    { value: FranchiseSuggestionScope.Unlinked, label: 'No franchise yet' },
  ];

  const rows = ref<FranchiseSuggestion[]>([]);
  const total = ref(0);
  const offset = ref(0);
  const loading = ref(false);
  const listError = ref('');
  const dismissedKeys = ref(new Set<string>());
  const busyKey = ref<string | null>(null);

  let latestRequest = 0;

  async function loadList() {
    const request = ++latestRequest;
    loading.value = true;
    listError.value = '';
    try {
      const res = await $api<PaginatedResponse<FranchiseSuggestion[]>>('admin/franchise/suggestions', {
        query: {
          query: query.value.trim() || undefined,
          mediaType: mediaType.value ?? undefined,
          scope: scope.value,
          offset: offset.value,
          limit: PAGE_SIZE,
        },
      });
      if (request !== latestRequest) return;
      rows.value = res.data;
      total.value = res.totalItems;
      dismissedKeys.value = new Set();
    } catch (e) {
      if (request !== latestRequest) return;
      listError.value = apiErrorMessage(e, 'The suggestions could not be loaded.');
    } finally {
      if (request === latestRequest) loading.value = false;
    }
  }

  function reloadFromStart() {
    offset.value = 0;
    loadList();
  }

  watch(query, debounce(reloadFromStart, 300));
  watch([mediaType, scope], reloadFromStart);

  function onPage(ev: { first: number }) {
    offset.value = ev.first;
    loadList();
  }

  onMounted(loadList);

  const filtering = computed(() => !!query.value.trim() || mediaType.value != null || scope.value !== FranchiseSuggestionScope.All);

  const isolatedIds = (s: FranchiseSuggestion) => s.decks.filter((d) => d.franchiseId == null).map((d) => d.deck.deckId);

  function builderPath(s: FranchiseSuggestion): string {
    const anchorFranchise = s.decks.find((d) => d.deck.deckId === s.anchorDeckId)?.franchiseId ?? null;
    const add = s.decks
      .filter((d) => d.deck.deckId !== s.anchorDeckId && (anchorFranchise == null || d.franchiseId !== anchorFranchise))
      .map((d) => d.deck.deckId);
    return add.length ? `/dashboard/franchise/${s.anchorDeckId}?add=${add.join(',')}` : `/dashboard/franchise/${s.anchorDeckId}`;
  }

  async function setDismissed(s: FranchiseSuggestion, dismissed: boolean) {
    busyKey.value = s.rootKey;
    try {
      await $api(`admin/franchise/suggestions/${dismissed ? 'dismiss' : 'restore'}`, {
        method: 'POST',
        body: { rootKey: s.rootKey, deckIds: isolatedIds(s) },
      });
      const next = new Set(dismissedKeys.value);
      if (dismissed) next.add(s.rootKey);
      else next.delete(s.rootKey);
      dismissedKeys.value = next;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Not done', detail: apiErrorMessage(e, 'The change was not saved.'), life: 6000 });
    } finally {
      busyKey.value = null;
    }
  }

  function deckMeta(d: FranchiseSuggestionDeck): string {
    const year = releaseYearOf(d.deck.releaseDate);
    return [getMediaTypeText(d.deck.mediaType), year].filter(Boolean).join(', ');
  }

  function linksText(d: FranchiseSuggestionDeck): string {
    return d.linkTypes.length ? d.linkTypes.map(getLinkTypeText).join(', ') : 'No external links';
  }
</script>

<template>
  <div class="flex flex-col gap-4">
    <p class="m-0 max-w-3xl text-sm text-gray-600 dark:text-gray-400">
      Decks outside any franchise whose titles start like other decks. Open a match in the franchise builder to link it, or dismiss it if the titles are a
      coincidence.
    </p>

    <div class="flex flex-wrap items-center gap-2">
      <SearchInput v-model="query" placeholder="Search by root or deck title" aria-label="Search suggestions" class="w-full md:w-96" />
      <Select v-model="mediaType" :options="mediaTypeOptions" option-label="label" option-value="value" class="w-full sm:w-48" aria-label="Media type" />
      <Select v-model="scope" :options="scopeOptions" option-label="label" option-value="value" class="w-full sm:w-52" aria-label="Match kind" />
      <span v-if="!loading && !listError" class="text-sm text-gray-600 sm:ml-auto dark:text-gray-400">
        {{ total }} {{ total === 1 ? 'match' : 'matches' }}
      </span>
    </div>

    <Message v-if="listError" severity="error" :closable="false">
      <div class="flex flex-wrap items-center gap-3">
        <span>{{ listError }}</span>
        <Button label="Try again" size="small" severity="secondary" @click="loadList" />
      </div>
    </Message>

    <div v-if="loading && !rows.length" class="flex justify-center py-10" role="status" aria-label="Loading suggestions">
      <ProgressSpinner style="width: 40px; height: 40px" />
    </div>

    <p v-else-if="!listError && !rows.length" class="m-0 py-6 text-center text-gray-500 dark:text-gray-400">
      {{ filtering ? 'Nothing matches these filters.' : 'No suggestions. Every deck with a shared title root is already in a franchise or dismissed.' }}
    </p>

    <ul v-else class="m-0 flex list-none flex-col gap-3 p-0" :class="{ 'opacity-60': loading }" :aria-busy="loading">
      <li v-for="s in rows" :key="s.rootKey" class="rounded-lg border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
        <div v-if="dismissedKeys.has(s.rootKey)" class="flex flex-wrap items-center justify-between gap-2 px-4 py-3">
          <span class="text-sm text-gray-600 dark:text-gray-400">
            Dismissed <span class="font-medium text-gray-800 dark:text-gray-200" v-bind="japaneseTextAttrs(s.root)">{{ s.root }}</span>
          </span>
          <Button label="Undo" size="small" severity="secondary" text :loading="busyKey === s.rootKey" @click="setDismissed(s, false)" />
        </div>
        <template v-else>
          <div class="flex flex-wrap items-start justify-between gap-3 px-4 pt-3 pb-2">
            <div class="min-w-0">
              <h3 class="m-0 text-lg font-semibold break-words" v-bind="japaneseTextAttrs(s.root)">{{ s.root }}</h3>
              <p class="m-0 text-sm text-gray-600 dark:text-gray-400">
                {{ isolatedIds(s).length }} of {{ s.decks.length }} {{ s.decks.length === 1 ? 'deck' : 'decks' }} outside any franchise
              </p>
            </div>
            <div class="flex flex-wrap gap-2">
              <Button
                as="router-link"
                :to="builderPath(s)"
                label="Open in builder"
                icon="pi pi-sitemap"
                size="small"
                :aria-label="`Open ${s.root} in the franchise builder`"
              />
              <Button
                label="Dismiss"
                size="small"
                severity="secondary"
                outlined
                :loading="busyKey === s.rootKey"
                :aria-label="`Dismiss ${s.root}`"
                @click="setDismissed(s, true)"
              />
            </div>
          </div>
          <ul class="m-0 list-none p-0">
            <li
              v-for="d in s.decks"
              :key="d.deck.deckId"
              class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 border-t border-gray-100 px-4 py-2 dark:border-gray-800"
            >
              <div class="min-w-0">
                <NuxtLink
                  :to="`/decks/media/${d.deck.deckId}/detail`"
                  class="font-medium break-words hover:underline"
                  v-bind="japaneseTextAttrs(localiseTitle(d.deck))"
                >
                  {{ localiseTitle(d.deck) }}
                </NuxtLink>
                <div class="text-xs text-gray-500 dark:text-gray-400">{{ deckMeta(d) }}. {{ linksText(d) }}</div>
              </div>
              <NuxtLink
                v-if="d.franchiseId != null"
                :to="`/franchise/${d.franchiseId}`"
                class="text-sm text-primary-700 hover:underline dark:text-primary-300"
                v-bind="japaneseTextAttrs(d.franchiseTitles ? localiseTitle(d.franchiseTitles) : null)"
              >
                {{ d.franchiseTitles ? localiseTitle(d.franchiseTitles) : `Franchise #${d.franchiseId}` }}
              </NuxtLink>
              <span v-else class="text-sm font-medium text-amber-800 dark:text-amber-300">No franchise</span>
            </li>
          </ul>
        </template>
      </li>
    </ul>

    <Paginator v-if="total > PAGE_SIZE" :first="offset" :rows="PAGE_SIZE" :total-records="total" class="bg-transparent!" @page="onPage" />
  </div>
</template>
