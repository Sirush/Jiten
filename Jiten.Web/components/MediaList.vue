<script setup lang="ts">
  import { useApiFetchPaginated } from '~/composables/useApiFetch';
  import { type Deck, MediaType, SortOrder, type Word } from '~/types';
  import Skeleton from 'primevue/skeleton';
  import Card from 'primevue/card';
  import InputText from 'primevue/inputtext';
  import { debounce } from 'perfect-debounce';

  const props = defineProps<{
    word?: Word;
  }>();

  const route = useRoute();
  const router = useRouter();

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const mediaType = computed(() => (route.query.mediaType ? route.query.mediaType : null));

  const titleFilter = ref(route.query.title ? route.query.title : null);
  const debouncedTitleFilter = ref(titleFilter.value);

  const sortByOptions = ref([
    { label: 'Title', value: 'title' },
    { label: 'Difficulty', value: 'difficulty' },
    { label: 'Character Count', value: 'charCount' },
    { label: 'Average Sentence Length', value: 'sentenceLength' },
    { label: 'Word Count', value: 'wordCount' },
    { label: 'Unique Kanji', value: 'uKanji' },
    { label: 'Unique Word Count', value: 'uWordCount' },
    { label: 'Unique Kanji Used Once', value: 'uKanjiOnce' },
  ]);

  const sortOrder = ref(route.query.sortOrder ? route.query.sortOrder : SortOrder.Ascending);

  const sortBy = ref(route.query.sortBy ? route.query.sortBy : sortByOptions.value[0].value);

  const updateDebounced = debounce(async (newValue) => {
    debouncedTitleFilter.value = newValue;
    await router.replace({
      query: {
        ...route.query,
        title: newValue || undefined,
        sortBy: 'filter',
        offset: 0,
      },
    });
    sortBy.value = 'filter';
  }, 300);

  watch(titleFilter, (newValue) => {
    updateDebounced(newValue);
  });

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

  const url = computed(() => `media-deck/get-media-decks`);

  const {
    data: response,
    status,
    error,
  } = await useApiFetchPaginated<Deck[]>(url, {
    query: {
      offset: offset,
      mediaType: mediaType,
      wordId: props?.word?.wordId,
      readingIndex: props?.word?.mainReading?.readingIndex,
      titleFilter: debouncedTitleFilter,
      sortBy: sortBy,
      sortOrder: sortOrder,
    },
    watch: [offset, mediaType],
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

  const scrollToTop = () => {
    nextTick(() => {
      window.scrollTo({ top: 0, behavior: 'instant' });
    });
  };

  const mediaTypeOptions = [
    { type: null, label: 'All' },
    { type: MediaType.Anime, label: 'Anime' },
    { type: MediaType.Drama, label: 'Dramas' },
    { type: MediaType.Manga, label: 'Manga' },
    { type: MediaType.Movie, label: 'Movies' },
    { type: MediaType.Novel, label: 'Novels' },
    { type: MediaType.NonFiction, label: 'Non-Fiction' },
    { type: MediaType.VideoGame, label: 'Video Games' },
    { type: MediaType.VisualNovel, label: 'Visual Novels' },
    { type: MediaType.WebNovel, label: 'Web Novels' },
  ];

  const isActive = (type: MediaType | null) => {
    if (type === null) {
      return !mediaType.value || mediaType.value === '0';
    }
    return Number(mediaType.value) === type;
  };
</script>

<template>
  <div class="flex flex-col gap-4">
    <Card>
      <template #content>
        <div class="flex flex-row flex-wrap justify-around gap-2">
          <NuxtLink
            v-for="option in mediaTypeOptions"
            :key="option.label"
            :to="{ query: option.type ? { mediaType: option.type } : {} }"
            :class="{ 'font-bold !text-purple-500': isActive(option.type) }"
          >
            {{ option.label }}
          </NuxtLink>
        </div>
      </template>
    </Card>
    <div class="flex flex-col md:flex-row gap-2">
      <div class="flex flex-row gap-2">
        <FloatLabel variant="on" class="w-full">
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
          <Icon
            v-if="sortOrder == SortOrder.Descending"
            name="mingcute:az-sort-descending-letters-line"
            size="1.25em"
          />
          <Icon v-if="sortOrder == SortOrder.Ascending" name="mingcute:az-sort-ascending-letters-line" size="1.25em" />
        </Button>
      </div>

      <IconField class="w-full">
        <InputIcon>
          <Icon name="material-symbols:search-rounded" />
        </InputIcon>
        <InputText v-model="titleFilter" type="text" placeholder="Search by name" class="w-full" />
        <InputIcon v-if="titleFilter" class="cursor-pointer" @click="titleFilter = null">
          <Icon name="material-symbols:close" />
        </InputIcon>
      </IconField>
    </div>
    <div>
      <div class="flex flex-col gap-1">
        <div class="flex flex-col md:flex-row justify-between">
          <div class="flex gap-8 pl-2">
            <NuxtLink
              :to="previousLink"
              :class="previousLink == null ? '!text-gray-500 pointer-events-none' : ''"
              no-rel
            >
              Previous
            </NuxtLink>
            <NuxtLink :to="nextLink" :class="nextLink == null ? '!text-gray-500 pointer-events-none' : ''" no-rel>
              Next
            </NuxtLink>
          </div>
          <div class="pr-2 text-gray-500 dark:text-gray-300">
            viewing decks {{ start }}-{{ end }} from {{ totalItems }}
            total
          </div>
        </div>

        <div v-if="status === 'pending'" class="flex flex-col gap-4">
          <Card v-for="i in 5" :key="i" class="p-2">
            <template #content>
              <Skeleton width="100%" height="250px" />
            </template>
          </Card>
        </div>

        <div v-else-if="error">Error: {{ error }}</div>

        <div v-else class="flex flex-col gap-2">
          <MediaDeckCard v-for="deck in response.data" :key="deck.id" :deck="deck" />
        </div>
      </div>
      <div class="flex gap-8 pl-2">
        <NuxtLink
          :to="previousLink"
          :class="previousLink == null ? '!text-gray-500 pointer-events-none' : ''"
          @click="scrollToTop"
          no-rel
        >
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
  </div>
</template>

<style scoped></style>
