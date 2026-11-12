<script setup lang="ts">
  import { type Deck, SortOrder } from '~/types';
  import type { PaginatedResponse } from '~/types/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import type { OnboardingPath, WordCheckLevel } from '~/types/wordCheck';
  import { DeckDownloadType, DeckOrder, MediaType, StudyDeckType } from '~/types/enums';
  import { useSrsStore } from '~/stores/srsStore';
  import { useAuthStore } from '~/stores/authStore';
  import { ownProfileHandle, profilePath } from '~/utils/profileAppearance';

  const props = defineProps<{ level: WordCheckLevel | null }>();
  const emit = defineEmits<{ back: []; path: [path: OnboardingPath] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const srsStore = useSrsStore();
  const localiseTitle = useLocaliseTitle();
  const { youngWordsAmount, matureWordsAmount, masteredWordsAmount, wordSetMasteredWords, fetchKnownWordsAmount } = useVocabularyStats();
  const handle = computed(() => ownProfileHandle(useAuthStore().user));
  const creatingDeck = ref(false);
  const knowsNoWords = ref(false);

  const COMMON_WORDS_MAX_RANK = 10000;

  const beginner = computed(() => props.level === 'Starting' || knowsNoWords.value);
  const decks = ref<Deck[] | null>(null);
  const failed = ref(false);

  const sort = computed(() =>
    beginner.value ? { sortBy: 'difficulty', sortOrder: SortOrder.Ascending } : { sortBy: 'coverage', sortOrder: SortOrder.Descending }
  );
  const browseLink = computed(() => `/decks/media?sortBy=${sort.value.sortBy}&sortOrder=${sort.value.sortOrder}`);

  interface Feature {
    icon: string;
    title: string;
    description: string;
    to?: string;
    path?: OnboardingPath;
    suggested?: boolean;
    createsDeck?: boolean;
  }

  const byDifficulty: Feature = {
    icon: 'material-symbols:speed-outline-rounded',
    title: 'Titles by difficulty',
    description: 'Every title rated, from easiest to hardest.',
    to: `/decks/media?sortBy=difficulty&sortOrder=${SortOrder.Ascending}`,
  };
  const byCoverage: Feature = {
    icon: 'material-symbols:pie-chart-outline-rounded',
    title: 'Coverage',
    description: "How much of a title you'll understand before starting.",
    to: `/decks/media?sortBy=coverage&sortOrder=${SortOrder.Descending}`,
  };

  const groups = computed<{ title: string; features: Feature[] }[]>(() => [
    {
      title: 'Find titles',
      features: [
        ...(beginner.value ? [{ ...byDifficulty, suggested: true }, byCoverage] : [{ ...byCoverage, suggested: true }, byDifficulty]),
        {
          icon: 'material-symbols:format-list-bulleted-rounded',
          title: 'Community lists',
          description: 'Collection of media by other learners.',
          to: '/lists',
        },
      ],
    },
    {
      title: 'Learn new words',
      features: [
        beginner.value
          ? {
              icon: 'material-symbols:school-outline-rounded',
              title: 'Most common words',
              description: 'A study deck of the most common words, with new ones every day.',
              path: 'srs',
              suggested: true,
              createsDeck: true,
            }
          : {
              icon: 'material-symbols:school-outline-rounded',
              title: 'Study words from a title',
              description: 'Add a title as a study deck and learn its new words every day.',
              to: browseLink.value,
              path: 'srs',
              suggested: true,
            },
        {
          icon: 'material-symbols:download-rounded',
          title: 'Anki decks',
          description: "Download the vocabulary from a title with only your unknown words.",
          to: browseLink.value,
          path: 'anki',
        },
        {
          icon: 'material-symbols:grid-view-outline-rounded',
          title: 'Kanji grid',
          description: 'All the kanji, ranked by your knowledge of them.',
          to: '/kanji-grid',
        },
      ],
    },
    {
      title: 'While reading',
      features: [
        {
          icon: 'material-symbols:visibility-outline-rounded',
          title: 'Jiten Reader',
          description: "Read directly in your browser while highlighting the words you don't know and review them.",
          to: '/reader',
          path: 'reader',
          suggested: !beginner.value,
        },
        {
          icon: 'material-symbols:dictionary-outline-rounded',
          title: 'Frequency dictionaries',
          description: 'Get some frequency dictionaries for Yomitan, so you can see how common some words are anywhere on the web.',
          to: '/frequency-dictionaries',
        },
        {
          icon: 'material-symbols:smart-display-outline-rounded',
          title: 'JitenMPV',
          description: 'Watch anime and dramas in mpv with coloured subtitles, and review or mine words as you go.',
          to: 'https://github.com/Sirush/JitenMPV/releases',
        },
      ],
    },
    {
      title: 'Keep track',
      features: [
        {
          icon: 'material-symbols:bookmark-outline-rounded',
          title: 'Media list',
          description: 'Keep a list of the titles you started, completed or are planning.',
          to: handle.value ? profilePath(handle.value, 'library') : '/profile',
        },
        {
          icon: 'material-symbols:account-circle-outline',
          title: 'Profile',
          description: 'Your own page, with your library, stats and progress in one place, that you can share with others.',
          to: handle.value ? profilePath(handle.value) : '/profile',
        },
        {
          icon: 'material-symbols:monitoring-rounded',
          title: 'Review statistics',
          description: 'Your vocabulary growth, review history, retention over time and many more stats.',
          to: '/srs/stats',
        },
      ],
    },
  ]);

  function onFeatureClick(feature: Feature) {
    if (feature.createsDeck) createCommonWordsDeck();
    else if (feature.path) emit('path', feature.path);
  }

  const BEGINNER_MEDIA_TYPES = [MediaType.Anime, MediaType.Manga, MediaType.Novel];
  const BEGINNER_MAX_DIFFICULTY = 1.8;
  const BEGINNER_MAX_CHARACTERS = 200_000;

  async function fetchDecks(query: Record<string, unknown>) {
    const response = await $api<PaginatedResponse<Deck[]>>('media-deck/get-media-decks', { query });
    return response.data;
  }

  async function fetchBeginnerDecks() {
    const perType = await Promise.all(
      BEGINNER_MEDIA_TYPES.map((mediaType) =>
        fetchDecks({
          mediaType,
          sortBy: 'popularity',
          difficultyMax: BEGINNER_MAX_DIFFICULTY,
          charCountMax: BEGINNER_MAX_CHARACTERS,
          excludeSequels: true,
          limit: 1,
        })
      )
    );
    return perType.flatMap((typeDecks) => typeDecks.slice(0, 1));
  }

  async function load() {
    failed.value = false;
    try {
      const picks = beginner.value ? await fetchBeginnerDecks() : [];
      decks.value = picks.length ? picks : await fetchDecks({ ...sort.value, limit: 3 });
    } catch {
      failed.value = true;
    }
  }

  async function createCommonWordsDeck() {
    creatingDeck.value = true;
    try {
      await srsStore.addStudyDeck({
        deckType: StudyDeckType.GlobalDynamic,
        name: 'Most common words',
        downloadType: DeckDownloadType.Full,
        order: DeckOrder.GlobalFrequency,
        minFrequency: 0,
        maxFrequency: 0,
        minGlobalFrequency: 1,
        maxGlobalFrequency: COMMON_WORDS_MAX_RANK,
        excludeKana: false,
      });
      emit('path', 'srs');
      await navigateTo('/srs/decks');
    } catch (error) {
      toast.add({ severity: 'error', detail: extractApiError(error, 'Could not create the deck. Try again.'), life: 5000 });
    } finally {
      creatingDeck.value = false;
    }
  }

  onMounted(async () => {
    await fetchKnownWordsAmount();
    knowsNoWords.value = youngWordsAmount.value + matureWordsAmount.value + masteredWordsAmount.value + wordSetMasteredWords.value === 0;
    await load();
  });
</script>

<template>
  <div class="flex flex-col gap-6">
    <div class="flex flex-col gap-1.5">
      <h1 class="text-2xl md:text-3xl font-bold">{{ beginner ? 'Titles to start with' : 'You can now see your coverage on these titles' }}</h1>
      <p class="text-gray-600 dark:text-gray-300">
        {{ beginner ? 'Popular titles that are easy to start with.' : "Here's some of those with the highest coverage you could enjoy next." }}
      </p>
    </div>

    <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
      <template v-if="decks">
        <NuxtLink
          v-for="deck in decks"
          :key="deck.deckId"
          :to="`/decks/media/${deck.deckId}/detail`"
          target="_blank"
          class="flex gap-3.5 p-3.5 rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 hover:border-primary-400 !no-underline !text-inherit"
        >
          <img
            :src="coverUrl(deck.coverName, 'xs')"
            alt=""
            class="w-16 h-[90px] rounded object-cover shrink-0 bg-gray-200 dark:bg-neutral-800"
            loading="lazy"
          />
          <span class="flex-1 min-w-0 flex flex-col gap-1">
            <span v-bind="japaneseTextAttrs(localiseTitle(deck))" class="font-bold line-clamp-2">{{ localiseTitle(deck) }}</span>
            <span class="text-xs text-gray-600 dark:text-gray-300">{{ getMediaTypeText(deck.mediaType) }}</span>
            <span v-if="beginner" class="mt-auto flex flex-col gap-0.5 text-[13px]">
              <span class="flex justify-between gap-2">
                <span class="text-gray-600 dark:text-gray-300">Difficulty</span>
                <DifficultyDisplay :difficulty="deck.difficulty" :difficulty-raw="deck.difficultyRaw" />
              </span>
              <span class="flex justify-between gap-2">
                <span class="text-gray-600 dark:text-gray-300">Unique words</span>
                <span class="font-bold tabular-nums">{{ deck.uniqueWordCount.toLocaleString('en-US') }}</span>
              </span>
            </span>
            <span v-else-if="deck.coverage" class="mt-auto flex flex-col gap-1">
              <span class="flex justify-between text-[13px]">
                <span class="text-gray-600 dark:text-gray-300">Coverage</span>
                <span class="font-bold tabular-nums">{{ Math.round(deck.coverage) }}%</span>
              </span>
              <ProgressBar :value="deck.coverage" :show-value="false" class="!h-1.5" :aria-label="`Coverage ${Math.round(deck.coverage)}%`" />
            </span>
          </span>
        </NuxtLink>
      </template>
      <template v-else-if="!failed">
        <Skeleton v-for="i in 3" :key="i" height="7.5rem" border-radius="0.75rem" />
      </template>
    </div>
    <p v-if="failed" class="text-sm text-gray-600 dark:text-gray-300">
      Titles couldn't load. <Button label="Try again" link class="!p-0 !text-sm" @click="load" />
    </p>
    <p v-else-if="!beginner && decks && !decks.some((d) => d.coverage)" class="text-sm text-gray-600 dark:text-gray-300">
      Your coverage is still being calculated. The ranking fills in within a minute or two.
    </p>
    <NuxtLink :to="browseLink" target="_blank" class="self-start text-sm font-medium">
      {{ beginner ? 'Browse titles from easiest to hardest' : 'Browse all titles ranked for you' }}
    </NuxtLink>

    <div class="flex items-baseline justify-between gap-4 mt-2 -mb-3">
      <h2 class="text-lg font-bold">A few things Jiten can do</h2>
      <NuxtLink to="/features" target="_blank" class="text-sm font-medium shrink-0">See more features</NuxtLink>
    </div>
    <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
      <section v-for="group in groups" :key="group.title" class="p-2 rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900">
        <h3 class="px-2 pt-1.5 pb-1 text-[13px] font-semibold text-gray-600 dark:text-gray-300">{{ group.title }}</h3>
        <ul class="flex flex-col list-none m-0 p-0">
          <li
            v-for="feature in group.features"
            :key="feature.title"
            class="relative flex items-start gap-3 p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-neutral-800 has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-primary-500 has-[:disabled]:opacity-60"
          >
            <Icon :name="feature.icon" size="1.4em" class="shrink-0 mt-0.5 text-primary-600 dark:text-primary-300" />
            <span class="flex flex-col min-w-0">
              <span class="flex flex-wrap items-baseline gap-x-2">
                <button
                  v-if="feature.createsDeck"
                  type="button"
                  :disabled="creatingDeck"
                  class="font-bold text-left cursor-pointer disabled:cursor-default focus-visible:outline-none after:absolute after:inset-0"
                  @click="onFeatureClick(feature)"
                >
                  {{ feature.title }}
                </button>
                <NuxtLink
                  v-else
                  :to="feature.to"
                  target="_blank"
                  class="font-bold !text-inherit !no-underline focus-visible:outline-none after:absolute after:inset-0"
                  @click="onFeatureClick(feature)"
                >
                  {{ feature.title }}
                </NuxtLink>
                <span v-if="feature.suggested" class="text-xs font-semibold text-primary-700 dark:text-primary-300">Start here</span>
              </span>
              <span class="text-[13px] text-gray-600 dark:text-gray-300">{{ feature.description }}</span>
            </span>
          </li>
        </ul>
      </section>
    </div>

    <div class="flex items-center justify-between gap-4">
      <Button label="Back" severity="secondary" outlined @click="emit('back')" />
      <NuxtLink to="/" class="p-button no-underline">Finish</NuxtLink>
    </div>
  </div>
</template>
