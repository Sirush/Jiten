<script setup lang="ts">
  import { useApiFetchPaginated } from '~/composables/useApiFetch';
  import type { Deck, DeckDetail } from '~/types';
  import Card from 'primevue/card';
  import Skeleton from 'primevue/skeleton';

  const route = useRoute();

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const url = computed(() => `media-deck/${route.params.id}/detail`);

  const {
    data: response,
    status,
    error,
  } = await useApiFetchPaginated<DeckDetail[]>(url, {
    query: {
      offset: offset,
    },
    watch: [offset],
  });

  const currentPage = computed(() => response.value?.currentPage);
  const pageSize = computed(() => response.value?.pageSize);
  const totalItems = computed(() => response.value?.totalItems);

  const start = computed(() => (currentPage.value - 1) * pageSize.value + 1);
  const end = computed(() => Math.min(currentPage.value * pageSize.value, totalItems.value));

  const previousLink = computed(() => {
    return response.value?.hasPreviousPage
      ? { query: { ...route.query, offset: response.value.previousOffset } }
      : null;
  });

  const nextLink = computed(() => {
    return response.value?.hasNextPage ? { query: { ...route.query, offset: response.value.nextOffset } } : null;
  });

  const title = computed(() => {
    if (!response.value?.data) {
      return '';
    }

    return localiseTitle(response.value?.data.mainDeck);
  });

  useHead(() => {
    return {
      title: `${title.value} - Detail`,
      meta: [
        {
          name: 'description',
          content: `Detail for the deck ${title.value}`
        }]
    };
  });
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
    <div v-else>
      <MediaDeckCard :deck="response.data.mainDeck" />

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
          <div class="pr-2 text-gray-500 dark:text-gray-300">viewing decks {{ start }}-{{ end }} from {{ totalItems }} total</div>
        </div>
        <div class="flex flex-row flex-wrap gap-2 justify-center pt-4">
          <MediaDeckCard v-for="deck in response.data.subDecks" :key="deck.deckId" :deck="deck" :is-compact="true" />
        </div>
      </div>
      <div v-else class="pt-4">This deck has no subdecks</div>
    </div>
  </div>
</template>

<style scoped></style>
