<script setup lang="ts">
  import { DisplayStyle, MediaType } from '~/types';
  import { useApiFetchPaginated } from '~/composables/useApiFetch';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import type { DeckDetail, Deck } from '~/types';
  import Card from 'primevue/card';
  import Skeleton from 'primevue/skeleton';
  import { debounce } from 'perfect-debounce';

  // Without this the SPA answers 200 for any id (including the literal "*" from robots.txt patterns),
  // which Google indexes and then reports as soft 404s.
  definePageMeta({
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const route = useRoute();
  const router = useRouter();
  const deckId = computed(() => route.params.id as string);
  const localiseTitle = useLocaliseTitle();
  const authStore = useAuthStore();

  const showSeoBlocks = computed(() => !authStore.isAuthenticated);

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const url = computed(() => `media-deck/${route.params.id}/detail`);

  const appliedSubdeckFilter = computed(() => (route.query.subdeckFilter as string) || undefined);
  const subdeckSortQuery = computed(() => (route.query.subdeckSort as string) || undefined);
  const subdeckSortOrderQuery = computed(() => (route.query.subdeckSortOrder as string) || undefined);
  const subdeckPageSizeQuery = computed(() => {
    const size = Number(route.query.pageSize);
    return size === 50 || size === 100 ? size : undefined;
  });
  
  const minutesParam = (key: string) => {
    const value = Number(route.query[key]);
    return Number.isFinite(value) && value > 0 ? value : undefined;
  };
  const runtimeMinMinutes = computed(() => minutesParam('runtimeMin'));
  const runtimeMaxMinutes = computed(() => minutesParam('runtimeMax'));
  const runtimeMinSeconds = computed(() => (runtimeMinMinutes.value ? runtimeMinMinutes.value * 60 : undefined));
  const runtimeMaxSeconds = computed(() => (runtimeMaxMinutes.value ? runtimeMaxMinutes.value * 60 : undefined));
  const hideCompleted = computed(() => route.query.hideCompleted === '1');
  const hideCompletedParam = computed(() => (hideCompleted.value && authStore.isAuthenticated ? true : undefined));

  const {
    data: response,
    status,
    error,
    refresh: refreshDetail,
    ready: detailReady,
  } = await useApiFetchPaginated<DeckDetail>(url.value, {
    revalidateOnClient: true,
    query: {
      offset: offset,
      subdeckFilter: appliedSubdeckFilter,
      subdeckSort: subdeckSortQuery,
      subdeckSortOrder: subdeckSortOrderQuery,
      pageSize: subdeckPageSizeQuery,
      runtimeMin: runtimeMinSeconds,
      runtimeMax: runtimeMaxSeconds,
      hideCompleted: hideCompletedParam,
    },
    watch: [
      offset,
      deckId,
      appliedSubdeckFilter,
      subdeckSortQuery,
      subdeckSortOrderQuery,
      subdeckPageSizeQuery,
      runtimeMinSeconds,
      runtimeMaxSeconds,
      hideCompletedParam,
    ],
  });

  // A missing deck currently comes back as 200 with a null payload; 404 is accepted too so this keeps
  // working if the endpoint is tightened. Any other error (SSR timeout, 5xx) must keep rendering at
  // 200 rather than turning a transient API failure into a deindexed page.
  if (import.meta.server) {
    await detailReady;
    if (isMissingResource(error.value, response.value?.data)) throw createError({ statusCode: 404, statusMessage: 'Deck not found', fatal: true });
  }

  const { start, end, totalItems, previousLink, nextLink, currentPage, totalPages, pageLinkFor } = usePagination(response);

  const subdecksRefreshing = computed(() => status.value === 'pending');

  const subdeckFilterInput = ref(appliedSubdeckFilter.value ?? '');

  const pushSubdeckFilter = debounce(async (value: string) => {
    await router.replace({ query: { ...route.query, subdeckFilter: value.trim() || undefined, offset: undefined } });
  }, 500);

  const subdeckFilterComposing = ref(false);
  const onSubdeckFilterCompositionEnd = () => {
    subdeckFilterComposing.value = false;
    pushSubdeckFilter(subdeckFilterInput.value);
  };

  watch(subdeckFilterInput, (value) => {
    if (subdeckFilterComposing.value) return;
    if ((value.trim() || undefined) === appliedSubdeckFilter.value) return;
    pushSubdeckFilter(value);
  });

  // Keeps the box in step with the URL when the user navigates back into a filtered view.
  watch(appliedSubdeckFilter, (value) => {
    if ((subdeckFilterInput.value.trim() || undefined) !== value) subdeckFilterInput.value = value ?? '';
  });

  const mainDeckForControls = computed(() => response.value?.data?.mainDeck);
  const subdecksAreVideos = computed(() => mainDeckForControls.value?.mediaType === MediaType.YouTube);
  const subdecksHaveSpeech = computed(() => (mainDeckForControls.value?.speechDuration ?? 0) > 0);

  type SubdeckSortKey = 'relevance' | 'order' | 'difficulty' | 'length' | 'date' | 'speechspeed' | 'uniquewords' | 'coverage';
  const SORT_PARAM: Record<SubdeckSortKey, string | undefined> = {
    relevance: 'Relevance',
    order: undefined,
    difficulty: 'Difficulty',
    length: 'Length',
    date: 'Date',
    speechspeed: 'SpeechSpeed',
    uniquewords: 'UniqueWords',
    coverage: 'Coverage',
  };

  const subdeckSortOptions = computed(() => {
    const options: { label: string; value: string }[] = [];
    if (appliedSubdeckFilter.value) options.push({ label: 'Best match', value: 'relevance' });
    options.push({ label: 'Default order', value: 'order' }, { label: 'Reverse order', value: 'order-desc' });
    if (subdecksAreVideos.value) options.push({ label: 'Newest first', value: 'date-desc' }, { label: 'Oldest first', value: 'date' });
    options.push(
      { label: 'Easiest first', value: 'difficulty' },
      { label: 'Hardest first', value: 'difficulty-desc' },
      { label: 'Shortest first', value: 'length' },
      { label: 'Longest first', value: 'length-desc' }
    );
    if (subdecksHaveSpeech.value)
      options.push({ label: 'Slowest speech first', value: 'speechspeed' }, { label: 'Fastest speech first', value: 'speechspeed-desc' });
    options.push({ label: 'Fewest unique words', value: 'uniquewords' }, { label: 'Most unique words', value: 'uniquewords-desc' });
    if (authStore.isAuthenticated)
      options.push({ label: 'Highest coverage first', value: 'coverage-desc' }, { label: 'Lowest coverage first', value: 'coverage' });
    return options;
  });

  const subdeckSortValue = computed(() => {
    const raw = subdeckSortQuery.value?.toLowerCase();
    const known = raw && raw in SORT_PARAM ? (raw as SubdeckSortKey) : undefined;
    const sort = known ?? (appliedSubdeckFilter.value ? 'relevance' : 'order');
    return subdeckSortOrderQuery.value?.toLowerCase() === 'descending' ? `${sort}-desc` : sort;
  });

  const onSubdeckSortChange = (value: string) => {
    const descending = value.endsWith('-desc');
    const sort = (descending ? value.slice(0, -'-desc'.length) : value) as SubdeckSortKey;
    router.replace({
      query: {
        ...route.query,
        subdeckSort: SORT_PARAM[sort],
        subdeckSortOrder: descending ? 'Descending' : undefined,
        offset: undefined,
      },
    });
  };

  const showPageSize = computed(() => totalItems.value > 25 || subdeckPageSizeQuery.value !== undefined);

  const runtimeMinInput = ref<number | null>(runtimeMinMinutes.value ?? null);
  const runtimeMaxInput = ref<number | null>(runtimeMaxMinutes.value ?? null);
  const pushRuntime = debounce(async () => {
    const min = runtimeMinInput.value && runtimeMinInput.value > 0 ? runtimeMinInput.value : undefined;
    const max = runtimeMaxInput.value && runtimeMaxInput.value > 0 ? runtimeMaxInput.value : undefined;
    if (min === runtimeMinMinutes.value && max === runtimeMaxMinutes.value) return;
    await router.replace({ query: { ...route.query, runtimeMin: min, runtimeMax: max, offset: undefined } });
  }, 400);
  watch([runtimeMinInput, runtimeMaxInput], () => pushRuntime());
  watch([runtimeMinMinutes, runtimeMaxMinutes], ([min, max]) => {
    if ((runtimeMinInput.value ?? undefined) !== min) runtimeMinInput.value = min ?? null;
    if ((runtimeMaxInput.value ?? undefined) !== max) runtimeMaxInput.value = max ?? null;
  });

  const hideCompletedModel = computed({
    get: () => hideCompleted.value,
    set: (value: boolean) => {
      router.replace({ query: { ...route.query, hideCompleted: value ? '1' : undefined, offset: undefined } });
    },
  });

  const hasActiveSubdeckFilters = computed(() => !!appliedSubdeckFilter.value || !!runtimeMinMinutes.value || !!runtimeMaxMinutes.value || hideCompleted.value);

  const subdeckStyleKey = computed(() => `jiten-subdeck-style-${response.value?.data?.mainDeck?.parentDeckId ?? deckId.value}`);
  const storedSubdeckStyle = ref<DisplayStyle | null>(null);
  const readStoredSubdeckStyle = () => {
    try {
      const raw = localStorage.getItem(subdeckStyleKey.value);
      storedSubdeckStyle.value = raw === null ? null : (Number(raw) as DisplayStyle);
    } catch {
      storedSubdeckStyle.value = null;
    }
  };
  onMounted(readStoredSubdeckStyle);
  watch(subdeckStyleKey, readStoredSubdeckStyle);
  const subdeckStyle = computed<DisplayStyle>({
    get: () => storedSubdeckStyle.value ?? DisplayStyle.Card,
    set: (value) => {
      storedSubdeckStyle.value = value;
      try {
        localStorage.setItem(subdeckStyleKey.value, String(value));
      } catch {
      }
    },
  });

  const isDesktop = ref(true);
  onMounted(() => {
    const query = window.matchMedia('(min-width: 768px)');
    isDesktop.value = query.matches;
    query.addEventListener('change', (e) => (isDesktop.value = e.matches));
  });
  const effectiveSubdeckStyle = computed(() => (isDesktop.value ? subdeckStyle.value : DisplayStyle.Card));

  const hasSubdecksToShow = computed(() => (response.value?.data?.subDecks?.length ?? 0) > 0);
  // The filter is server-side, so a zero-match page must still render the controls that produced it.
  const showSubdeckSection = ref(false);
  const showSubdeckControls = ref(false);
  watchEffect(() => {
    if (hasSubdecksToShow.value || hasActiveSubdeckFilters.value || subdeckFilterInput.value) showSubdeckSection.value = true;
    if (totalItems.value > 5 || hasActiveSubdeckFilters.value || subdeckFilterInput.value) showSubdeckControls.value = true;
  });
  const noMatchMessage = computed(() =>
    appliedSubdeckFilter.value ? `No subdecks match “${appliedSubdeckFilter.value}”.` : 'No subdecks match these filters.'
  );

  const jitenStore = useJitenStore();
  watch(
    () => jitenStore.coverageVersion,
    () => {
      refreshDetail();
    }
  );

  // A single-media refresh bumps the media root's version; refetch so the subdeck bars repaint too.
  const mediaRootId = computed(() => response.value?.data?.mainDeck?.parentDeckId ?? Number(deckId.value));
  watch([mediaRootId, () => jitenStore.deckCoverageVersions[mediaRootId.value] ?? 0], ([rootId, version], [prevRootId, prevVersion]) => {
    if (rootId === prevRootId && version > prevVersion) refreshDetail();
  });

  const updateMainDeck = (updatedDeck: Deck) => {
    if (response.value?.data?.mainDeck) {
      response.value = { ...response.value, data: { ...response.value.data, mainDeck: updatedDeck } };
    }
  };

  const updateSubDeck = (updatedDeck: Deck) => {
    if (response.value?.data?.subDecks) {
      const index = response.value.data.subDecks.findIndex((d) => d.deckId === updatedDeck.deckId);
      if (index !== -1) {
        const newSubDecks = [...response.value.data.subDecks];
        newSubDecks[index] = updatedDeck;
        response.value = { ...response.value, data: { ...response.value.data, subDecks: newSubDecks } };
      }
    }
  };

  const jumpToSimilar = () => {
    // Update the URL hash so the position is shareable, without a full route navigation.
    history.pushState(null, '', '#similar-media');
    document.getElementById('similar-media')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };

  // Honour a shared link that already points at the anchor (#similar-media in the URL on load).
  onMounted(() => {
    if (window.location.hash === '#similar-media') {
      nextTick(() => document.getElementById('similar-media')?.scrollIntoView({ block: 'start' }));
    }
  });

  useDeckViewBeacon(deckId);

  const updateParentStatus = (parentDeckId: number, status: import('~/types').DeckStatus) => {
    if (response.value?.data?.mainDeck && response.value.data.mainDeck.deckId === parentDeckId) {
      response.value = { ...response.value, data: { ...response.value.data, mainDeck: { ...response.value.data.mainDeck, status } } };
    }
  };

  const title = computed(() => {
    if (!response.value?.data) {
      return '';
    }

    let title = '';
    if (response.value?.data.parentDeck != null) title += localiseTitle(response.value?.data.parentDeck) + ' - ';

    title += localiseTitle(response.value?.data.mainDeck);

    return title;
  });

  const coverUrl = computed(() => {
    const cover = response.value?.data?.mainDeck?.coverName;
    return cover && cover !== 'nocover.jpg' ? cover : undefined;
  });

  const mainDeck = computed(() => response.value?.data?.mainDeck);
  const parentDeck = computed(() => response.value?.data?.parentDeck);

  const metaDescription = computed(() => {
    const d = mainDeck.value;
    if (!d) return '';
    const type = getMediaTypeText(d.mediaType);
    const orig = d.originalTitle ? ` (${d.originalTitle})` : '';
    const chars = d.characterCount ? d.characterCount.toLocaleString() : '';
    const words = d.uniqueWordCount ? d.uniqueWordCount.toLocaleString() : '';
    const stats = chars ? ` - ${chars} chars, ${words} unique words` : '';
    const tail = ' Difficulty, kanji & word stats on Jiten.';
    // Keep the description within ~160 chars (search engines truncate beyond this).
    // Drop the stats clause first, then hard-clamp the title at a word boundary.
    const build = (s: string) => `Vocabulary list & free Anki deck for ${title.value}${orig}, a Japanese ${type}${s}.${tail}`;
    const full = build(stats);
    if (full.length <= 160) return full;
    const trimmed = build('');
    if (trimmed.length <= 160) return trimmed;
    return (
      build('')
        .slice(0, 157)
        .replace(/\s+\S*$/, '') + '…'
    );
  });

  useSeoMeta({
    title: () => `${title.value} Vocabulary List & Anki Deck`,
    description: metaDescription,
    ogTitle: () => `${title.value} — Japanese Vocabulary List & Anki Deck`,
    ogDescription: metaDescription,
    ogType: 'article',
    twitterTitle: () => `${title.value} — Vocabulary List & Anki Deck`,
    twitterDescription: metaDescription,
  });

  useHead(() => ({
    link: coverUrl.value ? [{ rel: 'preload', as: 'image', href: coverUrl.value, fetchpriority: 'high' }] : [],
  }));

  // Subdecks (episodes, volumes) differ from their siblings only by number, so Google collapses them
  // onto the parent anyway; noindex keeps the crawl budget on parents while follow passes signal up.
  // Parents return undefined so the site-wide rule (which carries max-image-preview/max-snippet) stands.
  useRobotsRule(() => (parentDeck.value != null ? 'noindex, follow' : undefined));

  const pageUrl = computed(() => `https://jiten.moe/decks/media/${deckId.value}/detail`);
  useDeckSchema(mainDeck, pageUrl, parentDeck);

  // OG images are server-rendered only. Wait for the fetch to settle so the eager prop
  // snapshot below isn't empty (the wrapper's `await` above doesn't block on the request).
  if (import.meta.server) {
    await detailReady;
    const d = mainDeck.value;
    defineOgImage(
      'MediaDeckCardOgImage',
      {
        title: d ? d.originalTitle?.trim() || localiseTitle(d) : '',
        mediaType: d?.mediaType,
        coverName: d?.coverName,
        isLandscapeCover: d?.mediaType === MediaType.YouTube && !!d?.parentDeckId,
        characterCount: d?.characterCount,
        wordCount: d?.wordCount,
        uniqueWordCount: d?.uniqueWordCount,
        uniqueKanjiCount: d?.uniqueKanjiCount,
        uniqueKanjiUsedOnceCount: d?.uniqueKanjiUsedOnceCount,
        averageSentenceLength: d?.averageSentenceLength,
        hideAverageSentenceLength: d?.hideAverageSentenceLength,
        dialoguePercentage: d?.dialoguePercentage,
        hideDialoguePercentage: d?.hideDialoguePercentage,
        difficulty: d?.difficulty,
      },
      // Never cache a placeholder: if the SSR data fetch failed (e.g. transient rate limit)
      // the card renders "Loading…" — don't bake that into the multi-day CDN cache.
      d ? {} : { cacheMaxAgeSeconds: 0 }
    );
  }
</script>

<template>
  <div>
    <DeckBreadcrumb :deck="response?.data?.mainDeck" :parent-deck="response?.data?.parentDeck" class="mb-2" />
    <div v-if="status === 'pending' && !response?.data?.mainDeck" class="flex flex-col gap-4">
      <Card v-for="i in 5" :key="i" class="p-2">
        <template #content>
          <Skeleton width="100%" height="250px" />
        </template>
      </Card>
    </div>
    <div v-else-if="response?.data?.mainDeck">
      <MediaDeckCard :deck="response.data.mainDeck" title-tag="h1" hide-detail-button @update:deck="updateMainDeck" />

      <DeckStudyOverview v-if="showSeoBlocks && response.data.parentDeck == null" :deck="response.data.mainDeck" />

      <LazyCoverageJourneyCard :deck-id="response.data.mainDeck.deckId" />

      <div v-if="response.data.parentDeck != null" class="pt-4">
        This deck belongs to
        <NuxtLink :to="`/decks/media/${response.data.parentDeck.deckId}/detail`">
          {{ localiseTitle(response.data.parentDeck) }}
        </NuxtLink>
      </div>

      <div v-if="showSubdeckSection" class="pt-4">
        <div class="flex items-baseline justify-between gap-4">
          <h2 class="font-bold">Subdecks</h2>
          <a href="#similar-media" class="text-primary text-sm cursor-pointer" @click.prevent="jumpToSimilar">Jump to similar media ↓</a>
        </div>
        <div v-if="showSubdeckControls" class="flex flex-col gap-2 pt-2">
          <div class="flex flex-col sm:flex-row gap-2 sm:items-center">
            <IconField class="grow">
              <InputIcon>
                <Icon name="material-symbols:search-rounded" />
              </InputIcon>
              <InputText
                v-model="subdeckFilterInput"
                type="text"
                :placeholder="subdecksAreVideos ? 'Search videos by title or number' : 'Search subdecks by title or number'"
                aria-label="Search subdecks"
                class="w-full"
                @compositionstart="subdeckFilterComposing = true"
                @compositionend="onSubdeckFilterCompositionEnd"
              />
              <InputIcon v-if="subdeckFilterInput" class="cursor-pointer" @click="subdeckFilterInput = ''">
                <Icon name="material-symbols:close" />
              </InputIcon>
            </IconField>
            <div class="flex items-center gap-2 sm:contents">
              <Select
                :model-value="subdeckSortValue"
                :options="subdeckSortOptions"
                option-label="label"
                option-value="value"
                aria-label="Sort subdecks"
                class="grow sm:grow-0 sm:w-56"
                @update:model-value="onSubdeckSortChange"
              />
              <div class="hidden md:contents">
                <DisplayStyleSelector v-model="subdeckStyle" />
              </div>
              <label v-if="authStore.isAuthenticated" class="flex items-center gap-2 cursor-pointer text-sm whitespace-nowrap shrink-0">
                <Checkbox v-model="hideCompletedModel" binary input-id="hideCompletedSubdecks" />
                <span>Hide completed</span>
              </label>
            </div>
          </div>
          <div v-if="subdecksAreVideos" class="flex flex-wrap gap-x-4 gap-y-2 items-center text-sm">
            <div class="flex items-center gap-2">
              <span class="text-surface-600 dark:text-surface-300">Duration</span>
              <InputNumber
                v-model="runtimeMinInput"
                :min="0"
                :max="1440"
                placeholder="min"
                suffix=" min"
                aria-label="Minimum video length in minutes"
                input-class="w-24"
                size="small"
              />
              <span class="text-surface-400">to</span>
              <InputNumber
                v-model="runtimeMaxInput"
                :min="0"
                :max="1440"
                placeholder="max"
                suffix=" min"
                aria-label="Maximum video length in minutes"
                input-class="w-24"
                size="small"
              />
            </div>
          </div>
        </div>
        <div :class="{ 'opacity-50 transition-opacity': subdecksRefreshing }" :aria-busy="subdecksRefreshing">
          <div v-if="totalPages > 1 || showPageSize" class="pt-4 pb-1">
            <PaginationControls
              :previous-link="previousLink"
              :next-link="nextLink"
              :current-page="currentPage"
              :total-pages="totalPages"
              :page-link-for="pageLinkFor"
              :start="start"
              :end="end"
              :total-items="totalItems"
              item-label="decks"
              :page-size="25"
              :page-size-options="showPageSize ? [25, 50, 100] : undefined"
              page-size-param="pageSize"
              mobile-compact
            />
          </div>
          <div
            v-if="hasSubdecksToShow && effectiveSubdeckStyle === DisplayStyle.Card"
            class="grid grid-cols-[repeat(auto-fit,20rem)] items-stretch gap-2 justify-center pt-4"
          >
            <MediaDeckCard
              v-for="deck in response.data.subDecks"
              :key="deck.deckId"
              :deck="deck"
              title-tag="h3"
              :is-compact="true"
              @update:deck="updateSubDeck"
              @parent-status-changed="updateParentStatus"
            />
          </div>
          <div v-else-if="hasSubdecksToShow && effectiveSubdeckStyle === DisplayStyle.Compact" class="flex flex-wrap gap-4 justify-center pt-4">
            <MediaDeckCompactView v-for="(deck, index) in response.data.subDecks" :key="deck.deckId" :deck="deck" :lazy-cover="index >= 12" />
          </div>
          <div v-else-if="hasSubdecksToShow" class="flex flex-col gap-0.5 pt-4">
            <MediaDeckTableView v-for="(deck, index) in response.data.subDecks" :key="deck.deckId" :deck="deck" :lazy-render="index >= 12" />
          </div>
          <div v-else class="pt-6 text-center text-surface-500 dark:text-surface-400">{{ noMatchMessage }}</div>
        </div>
      </div>

      <div id="similar-media" class="scroll-mt-4">
        <SimilarMediaSection :deck="response.data.mainDeck" />
      </div>

      <DeckVocabularyHighlights v-if="showSeoBlocks && response.data.parentDeck == null" :key="response.data.mainDeck.deckId" :deck="response.data.mainDeck" />
    </div>
    <div v-else class="text-center py-12 flex flex-col items-center gap-4">
      <p class="text-surface-500 dark:text-surface-400">Failed to load this deck.</p>
      <Button label="Retry" icon="pi pi-refresh" @click="refreshDetail()" />
    </div>
  </div>
</template>

<style scoped></style>
