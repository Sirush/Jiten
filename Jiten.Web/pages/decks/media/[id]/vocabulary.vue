<script setup lang="ts">
  import { type DeckVocabularyList, SortOrder } from '~/types';

  const route = useRoute();
  const router = useRouter();

  const id = route.params.id;

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const url = computed(() => `media-deck/${id}/vocabulary`);

  const sortByOptions = ref([
    { label: 'Chronological', value: 'chrono' },
    { label: 'Deck Frequency', value: 'deckFreq' },
    { label: 'Global Frequency', value: 'globalFreq' },
  ]);

  const sortOrder = ref(route.query.sortOrder ? route.query.sortOrder : SortOrder.Ascending);
  const sortBy = ref(route.query.sortBy ? route.query.sortBy : sortByOptions.value[0].value);

  watch(sortOrder, (newValue) => {
    router.replace({
      query: {
        ...route.query,
        sortOrder: newValue,
      },
    });
  });

  watch(sortBy, (newValue) => {
    router.replace({
      query: {
        ...route.query,
        sortBy: newValue,
      },
    });
  });

  const {
    data: response,
    status,
    error,
  } = await useApiFetchPaginated<DeckVocabularyList>(url.value, {
    query: {
      offset: offset,
      sortBy: sortBy,
      sortOrder: sortOrder,
    },
    watch: [offset],
  });

  const currentPage = computed(() => response.value?.currentPage);
  const pageSize = computed(() => response.value?.pageSize);
  const totalItems = computed(() => response.value?.totalItems);

  const start = computed(() => (currentPage.value - 1) * pageSize.value + 1);
  const end = computed(() => Math.min(currentPage.value * pageSize.value, totalItems.value));

  const previousLink = computed(() => {
    return response.value?.hasPreviousPage ? { query: { offset: response.value.previousOffset } } : null;
  });
  const nextLink = computed(() => {
    return response.value?.hasNextPage ? { query: { offset: response.value.nextOffset } } : null;
  });

  const deckId = computed(() => {
    return response.value?.data.deck.deckId;
  });

  const title = computed(() => {
    if (!response.value?.data) {
      return '';
    }

    let title = '';
    if (response.value?.data.parentDeck != null) title += localiseTitle(response.value?.data.parentDeck) + ' - ';

    title += localiseTitle(response.value?.data.deck);

    return title;
  });

  useHead(() => {
    return {
      title: `${title.value} - Vocabulary`,
      meta: [
        {
          name: 'description',
          content: `Vocabulary list for ${title.value}`,
        },
      ],
    };
  });

  const scrollToTop = () => {
    nextTick(() => {
      window.scrollTo({ top: 0, behavior: 'instant' });
    });
  };
</script>

<template>
  <div class="flex flex-col gap-2">
    <div>
      Vocabulary for
      <NuxtLink :to="`/decks/media/${deckId}/detail`">
        {{ title }}
      </NuxtLink>
    </div>
    <div class="flex flex-row gap-2">
      <FloatLabel variant="on">
        <Select
          v-model="sortBy"
          :options="sortByOptions"
          option-label="label"
          option-value="value"
          placeholder="Sort by"
          input-id="sortBy"
          class="w-full md:w-56"
        />
        <label for="sortBy">Sort by</label>
      </FloatLabel>
      <Button
        @click="sortOrder = sortOrder === SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending"
        class="w-12"
      >
        <Icon v-if="sortOrder == SortOrder.Descending" name="mingcute:az-sort-descending-letters-line" size="1.25em" />
        <Icon v-if="sortOrder == SortOrder.Ascending" name="mingcute:az-sort-ascending-letters-line" size="1.25em" />
      </Button>
    </div>
    <div class="flex justify-between flex-col md:flex-row">
      <div class="flex gap-8 pl-2">
        <NuxtLink :to="previousLink" :class="previousLink == null ? '!text-gray-500 pointer-events-none' : ''" no-rel>
          Previous
        </NuxtLink>
        <NuxtLink :to="nextLink" :class="nextLink == null ? '!text-gray-500 pointer-events-none' : ''" no-rel>
          Next
        </NuxtLink>
      </div>
      <div class="text-gray-500 dark:text-gray-300">
        viewing words {{ start }}-{{ end }} from {{ totalItems }} total
      </div>
    </div>
    <div v-if="status === 'pending'" class="flex flex-col gap-2">
      <Card v-for="i in 10" :key="i" class="p-2">
        <template #content>
          <Skeleton width="100%" height="50px" />
        </template>
      </Card>
    </div>
    <div v-else-if="error">Error: {{ error }}</div>
    <div v-else class="flex flex-col gap-2">
      <VocabularyEntry
        v-for="word in response.data.words"
        :key="word.wordId"
        :word="word"
        :is-compact="true"
        @click="scrollToTop"
      />
    </div>
    <div class="flex gap-8 pl-2">
      <NuxtLink :to="previousLink" :class="previousLink == null ? '!text-gray-500 pointer-events-none' : ''" no-rel>
        Previous
      </NuxtLink>
      <NuxtLink
        :to="nextLink"
        :class="nextLink == null ? '!text-gray-500 pointer-events-none' : ''"
        @click="scrollToTop"
        no-rel
      >
        Next
      </NuxtLink>
    </div>
  </div>
</template>

<style scoped></style>
