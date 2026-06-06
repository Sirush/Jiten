<script setup lang="ts">
  import { useApiFetchPaginated } from '~/composables/useApiFetch';
  import { useJitenStore } from '~/stores/jitenStore';
  import type { DeckDetail, Deck } from '~/types';
  import Card from 'primevue/card';
  import Skeleton from 'primevue/skeleton';

  const route = useRoute();
  const deckId = computed(() => route.params.id as string);
  const localiseTitle = useLocaliseTitle();

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const url = computed(() => `media-deck/${route.params.id}/detail`);

  const {
    data: response,
    status,
    error,
    refresh: refreshDetail,
  } = await useApiFetchPaginated<DeckDetail>(url.value, {
    revalidateOnClient: true,
    query: {
      offset: offset,
    },
    watch: [offset, deckId],
  });

  const { start, end, totalItems, previousLink, nextLink } = usePagination(response);

  const jitenStore = useJitenStore();
  watch(() => jitenStore.coverageVersion, () => {
    refreshDetail();
  });

  const updateMainDeck = (updatedDeck: Deck) => {
    if (response.value?.data?.mainDeck) {
      response.value = { ...response.value, data: { ...response.value.data, mainDeck: updatedDeck } };
    }
  };

  const updateSubDeck = (updatedDeck: Deck) => {
    if (response.value?.data?.subDecks) {
      const index = response.value.data.subDecks.findIndex(d => d.deckId === updatedDeck.deckId);
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
    const chars = d.characterCount ? d.characterCount.toLocaleString() : '';
    const words = d.uniqueWordCount ? d.uniqueWordCount.toLocaleString() : '';
    return `Frequency-ordered vocabulary list and downloadable Anki deck for ${title.value}`
      + ` (${d.originalTitle ?? ''}), a Japanese ${type}`
      + (chars ? ` with ${chars} characters and ${words} unique words` : '')
      + `. Difficulty, kanji and word statistics on Jiten.`;
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
    link: coverUrl.value
      ? [{ rel: 'preload', as: 'image', href: coverUrl.value, fetchpriority: 'high' }]
      : [],
  }));

  const pageUrl = computed(() => `https://jiten.moe/decks/media/${deckId.value}/detail`);
  useDeckSchema(mainDeck, pageUrl, parentDeck);

  defineOgImageComponent('MediaDeckCardOgImage', { deckId: deckId });
</script>

<template>
  <div>
    <div v-if="status === 'pending'" class="flex flex-col gap-4">
      <Card v-for="i in 5" :key="i" class="p-2">
        <template #content>
          <Skeleton width="100%" height="250px" />
        </template>
      </Card>
    </div>
    <div v-else-if="response?.data?.mainDeck">
      <DeckBreadcrumb :deck="response.data.mainDeck" :parent-deck="response.data.parentDeck" class="mb-2" />
      <MediaDeckCard :deck="response.data.mainDeck" title-tag="h1" hide-detail-button @update:deck="updateMainDeck" />

      <div v-if="response.data.parentDeck != null" class="pt-4">
        This deck belongs to
        <NuxtLink :to="`/decks/media/${response.data.parentDeck.deckId}/detail`">
          {{ localiseTitle(response.data.parentDeck) }}
        </NuxtLink>
      </div>

      <div v-if="response.data.subDecks.length > 0" class="pt-4">
        <div class="flex items-baseline justify-between gap-4">
          <h2 class="font-bold">Subdecks</h2>
          <a href="#similar-media" class="text-primary text-sm cursor-pointer" @click.prevent="jumpToSimilar">
            Jump to similar media ↓
          </a>
        </div>
        <div v-if="previousLink != null || nextLink != null" class="flex flex-col md:flex-row justify-between">
          <div class="flex gap-8 pl-2">
            <NuxtLink :to="previousLink" :class="previousLink == null ? 'text-gray-500 pointer-events-none' : ''">
              Previous
            </NuxtLink>
            <NuxtLink :to="nextLink" :class="nextLink == null ? 'text-gray-500 pointer-events-none' : ''">
              Next
            </NuxtLink>
          </div>
          <div class="pr-2 text-gray-500 dark:text-gray-300">
            viewing decks {{ start }}-{{ end }} from {{ totalItems }}
            total
          </div>
        </div>
        <div class="flex flex-row flex-wrap gap-2 justify-center pt-4">
          <MediaDeckCard v-for="deck in response.data.subDecks" :key="deck.deckId" :deck="deck" title-tag="h3" :is-compact="true" @update:deck="updateSubDeck" @parent-status-changed="updateParentStatus" />
        </div>
      </div>
      <!--      <div v-else class="pt-4">This deck has no subdecks</div>-->

      <div id="similar-media" class="scroll-mt-4">
        <SimilarMediaSection :deck="response.data.mainDeck" />
      </div>
    </div>
  </div>
</template>

<style scoped></style>
