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

  useHead(() => {
    return {
      title: `${title.value} - Detail`,
      meta: [
        {
          name: 'description',
          content: `Anki deck, vocabulary list and statistics for ${title.value} (${response.value?.data?.mainDeck?.originalTitle ?? ''}).`,
        },
      ],
      link: coverUrl.value
        ? [{ rel: 'preload', as: 'image', href: coverUrl.value, fetchpriority: 'high' }]
        : [],
    };
  });

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
      <MediaDeckCard :deck="response.data.mainDeck" hide-detail-button @update:deck="updateMainDeck" />

      <div v-if="response.data.parentDeck != null" class="pt-4">
        This deck belongs to
        <NuxtLink :to="`/decks/media/${response.data.parentDeck.deckId}/detail`">
          {{ localiseTitle(response.data.parentDeck) }}
        </NuxtLink>
      </div>

      <div v-if="response.data.subDecks.length > 0" class="pt-4">
        <span class="font-bold">Subdecks</span>
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
          <MediaDeckCard v-for="deck in response.data.subDecks" :key="deck.deckId" :deck="deck" :is-compact="true" @update:deck="updateSubDeck" @parent-status-changed="updateParentStatus" />
        </div>
      </div>
      <!--      <div v-else class="pt-4">This deck has no subdecks</div>-->
    </div>
  </div>
</template>

<style scoped></style>
