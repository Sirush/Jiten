<script setup lang="ts">
  import { type Deck, MediaType, DeckStatus } from '~/types';
  import Card from 'primevue/card';
  import TieredMenu from 'primevue/tieredmenu';
  import { getChildrenCountText, getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { getLinkTypeText } from '~/utils/linkTypeMapper';
  import { getDeckStatusText } from '~/utils/deckStatusMapper';
  import { useJitenStore } from '~/stores/jitenStore';
  import { formatDateAsYyyyMmDd } from '~/utils/formatDateAsYyyyMmDd';
  import { useAuthStore } from '~/stores/authStore';
  import { useSrsStore } from '~/stores/srsStore';
  import { useConfirm } from 'primevue/useconfirm';

  const props = defineProps<{
    deck: Deck;
    isCompact?: boolean;
    hideControl?: boolean;
    hideDetailButton?: boolean;
  }>();

  const emit = defineEmits<{
    'update:deck': [deck: Deck];
    'parent-status-changed': [parentDeckId: number, status: DeckStatus];
  }>();

  const showDownloadDialog = ref(false);
  const showStudyDeckDialog = ref(false);
  const showIssueDialog = ref(false);
  const isDescriptionExpanded = ref(false);
  const showIgnoreOverlay = ref(false);
  const showCompletionDialog = ref(false);
  const completionSuggestions = ref<import('~/types/types').ComparisonSuggestionDto[]>([]);
  const completionComparisonIndex = ref(0);
  const menu = ref();
  const difficultyRef = ref<{ tooltip: string }>();

  const store = useJitenStore();
  const authStore = useAuthStore();
  const srs = useSrsStore();
  const localiseTitle = useLocaliseTitle();
  const confirm = useConfirm();

  const displayAdminFunctions = computed(() => store.displayAdminFunctions);
  const readingSpeed = computed(() => store.readingSpeed);
  const readingDuration = computed(() => Math.round(props.deck.characterCount / readingSpeed.value));
  const speechSpeed = computed(() => props.deck.speechSpeed ?? 0);

  const isAudioVisual = computed(() =>
    [MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio].includes(props.deck.mediaType)
  );

  const formattedSpeechDuration = computed(() => {
    if (props.deck.speechDuration <= 0) return '';
    const totalSeconds = Math.floor(props.deck.speechDuration / 1000);
    if (totalSeconds < 60) return `${totalSeconds}s`;
    const totalMinutes = Math.floor(totalSeconds / 60);
    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    if (hours === 0) return `${minutes}min`;
    if (minutes === 0) return `${hours}h`;
    return `${hours}h ${minutes}min`;
  });

  const toggleMenu = (event: Event) => {
    menu.value.toggle(event);
  };

  const { toggleFavourite, toggleIgnore: _toggleIgnore, cancelIgnore: _cancelIgnore, setStatus } = useDeckPreference(
    () => props.deck,
    (updated) => emit('update:deck', updated)
  );

  const toggleIgnore = async () => {
    const newState = await _toggleIgnore();
    if (newState !== null) {
      showIgnoreOverlay.value = newState;
    }
  };

  const cancelIgnore = async () => {
    await _cancelIgnore();
    showIgnoreOverlay.value = false;
  };

  const { fetchSuggestions, fetchRating } = useDifficultyVotes();
  const completionVoteTimestamps = ref<number[]>([]);
  const existingRating = ref<number | null>(null);
  const showCalibrationBanner = ref(false);
  let calibrationTimer: ReturnType<typeof setTimeout> | undefined;

  watch(showCompletionDialog, (newVal, oldVal) => {
    if (oldVal && !newVal && Math.random() < 0.25) {
      showCalibrationBanner.value = true;
      clearTimeout(calibrationTimer);
      calibrationTimer = setTimeout(() => { showCalibrationBanner.value = false; }, 8000);
    }
  });

  const ratingDeckId = ref(props.deck.deckId);

  const openRatingDialog = async (deckId?: number) => {
    ratingDeckId.value = deckId ?? props.deck.deckId;
    existingRating.value = null;
    showCompletionDialog.value = true;
    completionComparisonIndex.value = 0;
    const [rating, suggestions] = await Promise.all([
      fetchRating(ratingDeckId.value),
      fetchSuggestions(ratingDeckId.value),
    ]);
    existingRating.value = rating;
    completionSuggestions.value = suggestions.slice(0, 2).map(pair =>
      pair.deckA.id === ratingDeckId.value ? pair : { deckA: pair.deckB, deckB: pair.deckA },
    );
  };

  const { $api } = useNuxtApp();

  const completeParentDeck = async (parentDeckId: number) => {
    await $api(`/user/deck-preferences/${parentDeckId}/status`, {
      method: 'POST',
      body: { status: DeckStatus.Completed },
    });
    emit('parent-status-changed', parentDeckId, DeckStatus.Completed);
    if (authStore.isAuthenticated) {
      const rating = await fetchRating(parentDeckId);
      if (rating == null) openRatingDialog(parentDeckId);
    }
  };

  const handleMarkCompleted = async () => {
    const response = await setStatus(DeckStatus.Completed);

    if (response?.parentDeckId != null && response.parentStatus != null) {
      emit('parent-status-changed', response.parentDeckId, response.parentStatus);
    }

    if (response?.allChildrenCompleted && response.parentDeckId != null) {
      confirm.require({
        message: 'All entries in this series are completed. Mark the series as completed too?',
        header: 'Complete Series',
        icon: 'pi pi-check-circle',
        acceptLabel: 'Yes, complete it',
        rejectLabel: 'No, it\'s still ongoing',
        rejectProps: { severity: 'secondary' },
        accept: () => completeParentDeck(response.parentDeckId!),
      });
      return;
    }

    if (authStore.isAuthenticated && !props.deck.parentDeckId) {
      const rating = await fetchRating(props.deck.deckId);
      if (rating == null) openRatingDialog();
    }
  };

  const completionCurrentPair = computed(() =>
    completionComparisonIndex.value < completionSuggestions.value.length
      ? completionSuggestions.value[completionComparisonIndex.value]
      : null
  );

  const advanceCompletion = () => { completionComparisonIndex.value++; };

  const menuItems = computed(() => [
    {
      label: props.deck.isFavourite ? 'Unfavourite' : 'Favourite',
      icon: props.deck.isFavourite ? 'pi pi-star-fill' : 'pi pi-star',
      command: toggleFavourite,
    },
    {
      label: props.deck.isIgnored ? 'Unignore' : 'Ignore',
      icon: props.deck.isIgnored ? 'pi pi-eye' : 'pi pi-eye-slash',
      command: toggleIgnore,
    },
    {
      label: 'Study with SRS',
      icon: 'pi pi-play',
      visible: srs.srsEnrolled === true,
      command: () => { showStudyDeckDialog.value = true; },
    },
    {
      label: 'Rate difficulty',
      icon: 'pi pi-gauge',
      visible: props.deck.status === DeckStatus.Completed && !props.deck.parentDeckId,
      command: () => { openRatingDialog(); },
    },
    {
      label: 'Set status',
      icon: 'pi pi-flag',
      items: [
        {
          label: 'None',
          command: () => setStatus(DeckStatus.None),
        },
        {
          label: 'Planning',
          command: () => setStatus(DeckStatus.Planning),
        },
        {
          label: 'Ongoing',
          command: () => setStatus(DeckStatus.Ongoing),
        },
        {
          label: 'Completed',
          command: () => handleMarkCompleted(),
        },
        {
          label: 'Dropped',
          command: () => setStatus(DeckStatus.Dropped),
        },
      ],
    },
  ]);

  const statusColor = computed(() => {
    if (!props.deck.status || props.deck.status === DeckStatus.None) return '';

    switch (props.deck.status) {
      case DeckStatus.Planning:
        return 'text-gray-500';
      case DeckStatus.Ongoing:
        return 'text-yellow-500';
      case DeckStatus.Completed:
        return 'text-green-500';
      case DeckStatus.Dropped:
        return 'text-red-500';
      default:
        return '';
    }
  });

  const sortedLinks = computed(() => {
    if (!props.deck.links || props.deck.links.length === 0) return [];

    return [...props.deck.links].sort((a, b) => {
      const textA = getLinkTypeText(a.linkType);
      const textB = getLinkTypeText(b.linkType);
      return textA.localeCompare(textB);
    });
  });

  const toggleDescription = () => {
    isDescriptionExpanded.value = !isDescriptionExpanded.value;
  };

  const combinedCoverage = computed(() => Math.min(props.deck.coverage + props.deck.youngCoverage, 100));
  const combinedUniqueCoverage = computed(() => Math.min(props.deck.uniqueCoverage + props.deck.youngUniqueCoverage, 100));

  const coverageTooltip = computed(() =>
    `Mature: ${((props.deck.wordCount * props.deck.coverage) / 100).toFixed(0)} / ${props.deck.wordCount} (${props.deck.coverage.toFixed(1)}%)` +
    `\nYoung: ${((props.deck.wordCount * props.deck.youngCoverage) / 100).toFixed(0)} / ${props.deck.wordCount} (${props.deck.youngCoverage.toFixed(1)}%)` +
    `\nTotal: ${combinedCoverage.value.toFixed(1)}%`);
  const uniqueCoverageTooltip = computed(() =>
    `Mature: ${((props.deck.uniqueWordCount * props.deck.uniqueCoverage) / 100).toFixed(0)} / ${props.deck.uniqueWordCount} (${props.deck.uniqueCoverage.toFixed(1)}%)` +
    `\nYoung: ${((props.deck.uniqueWordCount * props.deck.youngUniqueCoverage) / 100).toFixed(0)} / ${props.deck.uniqueWordCount} (${props.deck.youngUniqueCoverage.toFixed(1)}%)` +
    `\nTotal: ${combinedUniqueCoverage.value.toFixed(1)}%`);

  const borderColor = computed(() => {
    if (!authStore.isAuthenticated || store.hideCoverageBorders || (props.deck.coverage == 0 && props.deck.uniqueCoverage == 0)) return 'none';
    return getCoverageBorder(props.deck.coverage);
  });


</script>

<template>
  <div class="relative" :class="isCompact ? 'w-80' : ''">
    <div
      v-if="showIgnoreOverlay"
      class="absolute inset-0 z-50 flex items-center justify-center backdrop-blur-lg bg-black/50 rounded-lg ignore-overlay"
      @click.stop
    >
      <div class="bg-white dark:bg-gray-800 rounded-lg p-6 max-w-md mx-4 shadow-xl">
        <p class="text-center text-gray-800 dark:text-gray-200 mb-4">This media will be ignored and no longer appear in search results.</p>
        <div class="text-center">
          <a
            href="#"
            class="text-primary-500 hover:text-primary-700 dark:hover:text-primary-400 font-semibold underline-offset-2 hover:underline"
            @click.prevent="cancelIgnore"
          >
            Cancel
          </a>
        </div>
      </div>
    </div>

    <Card class="p-2" :style="{ outline: borderColor }">
      <template #title>
        <div class="flex justify-between items-start">
          <span class="break-words min-w-0">{{ localiseTitle(deck) }}</span>
          <div class="flex flex-row items-center gap-1 h-6">
            <div v-if="authStore.isAuthenticated" class="flex items-center gap-2">
              <i v-if="deck.isFavourite" class="pi pi-star-fill text-yellow-500 text-lg" />
              <i v-if="deck.isIgnored" class="pi pi-eye-slash text-gray-800 dark:text-gray-300 text-lg" />
              <span v-if="deck.status && deck.status !== DeckStatus.None" :class="['text-sm font-bold', statusColor]">
                {{ getDeckStatusText(deck.status) }}
              </span>
            </div>
            <Tooltip content="View stats">
              <router-link
                :to="`/decks/media/${deck.deckId}/stats`"
                class="inline-block p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer"
              >
                <i class="pi pi-chart-bar text-primary-500" />
              </router-link>
            </Tooltip>
            <Tooltip v-if="authStore.isAuthenticated" content="More options">
              <button type="button" class="p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer" @click="toggleMenu">
                <i class="pi pi-ellipsis-v text-primary-500" />
              </button>
            </Tooltip>
          </div>
        </div>
      </template>
      <template v-if="!isCompact" #subtitle>{{ getMediaTypeText(deck.mediaType) }}</template>
      <template #content>
        <div class="flex-gap-6">
          <div class="flex-1 max-w-full overflow-hidden">
            <div class="flex flex-col md:flex-row gap-x-4 gap-y-2 w-full">
              <div v-if="!isCompact" class="text-left text-sm md:text-center">
                <img
                  :src="deck.coverName == 'nocover.jpg' ? '/img/nocover.jpg' : deck.coverName"
                  :alt="deck.originalTitle"
                  class="h-48 w-34 min-w-34 object-cover"
                  fetchpriority="high"
                  width="136"
                  height="192"
                />
                <div>{{ formatDateAsYyyyMmDd(new Date(deck.releaseDate)).replace(/-/g, '/') }}</div>
                <template v-if="authStore.isAuthenticated && (deck.coverage != 0 || deck.uniqueCoverage != 0)">
                  <Tooltip :content="coverageTooltip" block>
                    <div class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Coverage</div>
                    <div class="relative w-full bg-gray-400 dark:bg-gray-700 rounded-lg h-6 overflow-hidden">
                      <div class="absolute bg-purple-500/40 h-6 rounded-lg transition-all duration-700" :style="{ width: combinedCoverage.toFixed(1) + '%' }"></div>
                      <div class="absolute bg-purple-500 h-6 rounded-lg transition-all duration-700" :style="{ width: deck.coverage.toFixed(1) + '%' }"></div>
                      <span class="absolute inset-0 flex items-center pl-2 text-xs font-bold text-white dark:text-white z-10">
                        {{ deck.coverage.toFixed(1) }}%
                      </span>
                    </div>
                  </Tooltip>
                  <Tooltip :content="uniqueCoverageTooltip" block>
                    <div class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Unique coverage</div>
                    <div class="relative w-full bg-gray-400 dark:bg-gray-700 rounded-lg h-6 overflow-hidden">
                      <div class="absolute bg-purple-500/40 h-6 rounded-lg transition-all duration-700" :style="{ width: combinedUniqueCoverage.toFixed(1) + '%' }"></div>
                      <div class="absolute bg-purple-500 h-6 rounded-lg transition-all duration-700" :style="{ width: deck.uniqueCoverage.toFixed(1) + '%' }"></div>
                      <span class="absolute inset-0 flex items-center pl-2 text-xs font-bold text-white dark:text-white z-10">
                        {{ deck.uniqueCoverage.toFixed(1) }}%
                      </span>
                    </div>
                  </Tooltip>
                </template>
              </div>
              <div class="@container min-w-0 flex-1">
                <div
                  class="grid grid-cols-1 gap-x-6 gap-y-2 max-w-[51rem]"
                  :class="isCompact ? '' : '@xl:grid-cols-2 @3xl:grid-cols-3'"
                >
                  <div class="min-w-0">
                    <div v-if="isAudioVisual && deck.speechDuration > 0" class="flex justify-between flex-wrap stat-row">
                      <Tooltip :content="'Total duration of speech, excluding silence.\nCharacter count: ' + deck.characterCount.toLocaleString()">
                        <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Speech duration</span>
                      </Tooltip>
                      <span class="tabular-nums font-semibold">{{ formattedSpeechDuration }}</span>
                    </div>
                    <div v-else class="flex justify-between flex-wrap stat-row">
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Character count</span>
                      <span class="tabular-nums font-semibold">{{ deck.characterCount.toLocaleString() }}</span>
                    </div>
                    <div class="flex justify-between flex-wrap stat-row">
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Word count</span>
                      <span class="tabular-nums font-semibold">{{ deck.wordCount.toLocaleString() }}</span>
                    </div>
                    <div class="flex justify-between flex-wrap stat-row">
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Unique words</span>
                      <span class="tabular-nums font-semibold">{{ deck.uniqueWordCount.toLocaleString() }}</span>
                    </div>
                    <div class="flex justify-between flex-wrap stat-row">
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Words (1-occurrence)</span>
                      <span class="tabular-nums font-semibold">{{ deck.uniqueWordUsedOnceCount.toLocaleString() }}</span>
                    </div>
                  </div>

                  <div class="min-w-0">
                    <div class="flex justify-between flex-wrap stat-row">
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Unique kanji</span>
                      <span class="tabular-nums font-semibold">{{ deck.uniqueKanjiCount.toLocaleString() }}</span>
                    </div>
                    <div class="flex justify-between flex-wrap stat-row">
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Kanji (1-occurrence)</span>
                      <span class="tabular-nums font-semibold">{{ deck.uniqueKanjiUsedOnceCount.toLocaleString() }}</span>
                    </div>
                    <div v-if="deck.averageSentenceLength !== 0 && !deck.hideAverageSentenceLength" class="flex justify-between flex-wrap stat-row">
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Average sentence length</span>
                      <span class="tabular-nums font-semibold">{{ deck.averageSentenceLength.toFixed(1) }}</span>
                    </div>
                    <div v-if="speechSpeed > 0" class="flex justify-between flex-wrap stat-row">
                      <Tooltip content="Average speed of speech in mora per minute.">
                        <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Speech speed</span>
                      </Tooltip>
                      <span class="tabular-nums font-semibold">{{ speechSpeed.toFixed(0) }}</span>
                    </div>

                    <div v-if="deck.difficulty != -1" class="stat-row cursor-help">
                      <Tooltip :content="difficultyRef?.tooltip ?? ''" block>
                        <div class="flex justify-between">
                          <span class="text-gray-600 dark:text-gray-300 pr-2 font-normal shrink-0">
                            Difficulty
                            <i class="pi pi-info-circle text-primary-400 text-xs ml-0.5" />
                          </span>
                          <DifficultyDisplay ref="difficultyRef" :difficulty="deck.difficulty" :difficulty-raw="deck.difficultyRaw" :difficulty-algorithmic="deck.difficultyAlgorithmic" :user-adjustment="deck.userAdjustment" :vote-count="deck.distinctVoterCount || 0" />
                        </div>
                      </Tooltip>
                    </div>
                  </div>

                  <div class="min-w-0">
                    <div
                      v-if="!deck.hideDialoguePercentage && deck.dialoguePercentage != 0 && deck.dialoguePercentage != 100"
                      class="flex justify-between flex-wrap stat-row"
                    >
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Dialogue</span>
                      <span class="tabular-nums font-semibold">{{ deck.dialoguePercentage.toFixed(1) }}%</span>
                    </div>

                    <div v-if="deck.childrenDeckCount != 0" class="flex justify-between flex-wrap stat-row">
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">{{ getChildrenCountText(deck.mediaType) }}</span>
                      <span class="tabular-nums font-semibold">{{ deck.childrenDeckCount.toLocaleString() }}</span>
                    </div>

                    <div
                      v-if="
                        deck.mediaType == MediaType.Novel ||
                        deck.mediaType == MediaType.NonFiction ||
                        deck.mediaType == MediaType.VisualNovel ||
                        deck.mediaType == MediaType.WebNovel
                      "
                      class="flex justify-between flex-wrap stat-row"
                    >
                      <Tooltip
                        :content="
                          'Based on your reading speed of:\n ' +
                          '<strong>' +
                          readingSpeed +
                          '</strong>' +
                          ' characters per hour.\n<i>You can adjust it in the quick settings cog at the top right.</i>'
                        "
                      >
                        <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">
                          Duration
                          <i class="pi pi-info-circle cursor-pointer text-primary-500" />
                        </span>
                      </Tooltip>

                      <span class="tabular-nums font-semibold">{{ readingDuration > 0 ? readingDuration : '<1' }} h</span>
                    </div>

                    <div v-if="deck.externalRating != 0 && !store.hideExternalRating" class="flex justify-between flex-wrap stat-row">
                      <Tooltip content="Score based on user ratings from 3rd party websites, such as AniList, TMDB, VNDB or IGDB.">
                        <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">External Rating</span>
                      </Tooltip>
                      <span class="tabular-nums font-semibold">{{ deck.externalRating }} %</span>
                    </div>

                    <div v-if="deck.selectedWordOccurrences != 0" class="flex justify-between flex-wrap stat-row">
                      <span class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Appears (times)</span>
                      <span class="tabular-nums font-bold">{{ deck.selectedWordOccurrences.toLocaleString() }}</span>
                    </div>
                  </div>
                </div>

                <div class="mt-2">
                  <div v-if="deck.description && !store.hideDescriptions" class="description-container" :class="{ expanded: isDescriptionExpanded }">
                    <p class="whitespace-pre-line mb-0 text-sm">{{ deck.description }}</p>
                    <a v-if="deck.description.length > 50" href="#" class="text-primary-500 hover:text-primary-700 text-sm" @click.prevent="toggleDescription">
                      {{ isDescriptionExpanded ? 'View less' : 'View more' }}
                    </a>
                  </div>
                </div>

                <ExampleSentenceEntry v-if="deck.exampleSentence != undefined" :example-sentence="deck.exampleSentence" />

                <div v-if="deck.genres?.length || deck.tags?.length || deck.relationships?.length" class="mt-4 space-y-2">
                  <GenreTagDisplay v-if="!store.hideGenres && deck.genres?.length" :genres="deck.genres" label="Genres" />
                  <GenreTagDisplay v-if="!store.hideTags && deck.tags?.length" :tags="deck.tags" label="Tags" />
                  <RelatedMediaDisplay v-if="!store.hideRelations && deck.relationships?.length" :relationships="deck.relationships" />
                </div>

                <div v-if="sortedLinks.length" class="mt-4 flex flex-col md:flex-row gap-4">
                  <a v-for="link in sortedLinks" :key="link.url" :href="link.url" target="_blank">{{ getLinkTypeText(link.linkType) }}</a>
                </div>
                <template v-if="isCompact && authStore.isAuthenticated && (deck.coverage != 0 || deck.uniqueCoverage != 0)">
                  <Tooltip :content="coverageTooltip" block>
                    <div class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Coverage</div>
                    <div class="relative w-full bg-gray-400 dark:bg-gray-700 rounded-lg h-6 overflow-hidden">
                      <div class="absolute bg-purple-500/40 h-6 rounded-lg transition-all duration-700" :style="{ width: combinedCoverage.toFixed(1) + '%' }"></div>
                      <div class="absolute bg-purple-500 h-6 rounded-lg transition-all duration-700" :style="{ width: deck.coverage.toFixed(1) + '%' }"></div>
                      <span class="absolute inset-0 flex items-center pl-2 text-xs font-bold text-white dark:text-white z-10">
                        {{ deck.coverage.toFixed(1) }}%
                      </span>
                    </div>
                  </Tooltip>
                  <Tooltip :content="uniqueCoverageTooltip" block>
                    <div class="text-gray-600 dark:text-gray-300 truncate pr-2 font-normal">Unique coverage</div>
                    <div class="relative w-full bg-gray-400 dark:bg-gray-700 rounded-lg h-6 overflow-hidden">
                      <div class="absolute bg-purple-500/40 h-6 rounded-lg transition-all duration-700" :style="{ width: combinedUniqueCoverage.toFixed(1) + '%' }"></div>
                      <div class="absolute bg-purple-500 h-6 rounded-lg transition-all duration-700" :style="{ width: deck.uniqueCoverage.toFixed(1) + '%' }"></div>
                      <span class="absolute inset-0 flex items-center pl-2 text-xs font-bold text-white dark:text-white z-10">
                        {{ deck.uniqueCoverage.toFixed(1) }}%
                      </span>
                    </div>
                  </Tooltip>
                </template>
                <div v-if="!hideControl" class="mt-4">
                  <div class="flex gap-2" :class="[isCompact ? 'flex-row' : 'flex-col md:flex-row', { 'justify-center': isCompact }]">
                    <Tooltip v-if="!hideDetailButton" content="Details">
                      <Button
                        as="router-link"
                        :to="`/decks/media/${deck.deckId}/detail`"
                        :label="isCompact ? undefined : 'Details'"
                        class="text-center"
                        icon="pi pi-eye"
                      />
                    </Tooltip>
                    <Tooltip content="Vocabulary">
                      <Button
                        as="router-link"
                        :to="`/decks/media/${deck.deckId}/vocabulary`"
                        :label="isCompact ? undefined : 'Vocabulary'"
                        class="text-center"
                        icon="pi pi-book"
                      />
                    </Tooltip>
                    <Tooltip v-if="srs.srsEnrolled === true" content="Study with SRS">
                      <Button
                        :label="isCompact ? undefined : 'Study'"
                        class="text-center shadow-[inset_0_-1px_0_0_rgba(255,255,255,0.7)]"
                        icon="pi pi-play"
                        @click="showStudyDeckDialog = true"
                      />
                    </Tooltip>
                    <Tooltip content="Download / Learn">
                      <Button
                        :label="isCompact ? undefined : 'Download / Learn'"
                        class="text-center"
                        icon="pi pi-download"
                        @click="showDownloadDialog = true"
                      />
                    </Tooltip>
                    <Button
                      v-if="!isCompact && authStore.isAdmin && displayAdminFunctions"
                      as="router-link"
                      :to="`/dashboard/media/${deck.deckId}`"
                      label="Edit"
                      icon="pi pi-pencil"
                      class="text-center"
                    />
                    <Button
                      v-if="!isCompact && authStore.isAuthenticated"
                      label="Report an issue"
                      icon="pi pi-exclamation-triangle"
                      class="text-center"
                      @click="showIssueDialog = true"
                    />
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </template>
    </Card>

    <LazyMediaDeckDownloadDialog v-if="showDownloadDialog" :deck="deck" :visible="showDownloadDialog" @update:visible="showDownloadDialog = $event" />
    <LazySrsAddDeckDialog v-if="showStudyDeckDialog" :visible="showStudyDeckDialog" :preselected-deck="deck" @update:visible="showStudyDeckDialog = $event" />
    <LazyReportIssueDialog v-if="showIssueDialog" :visible="showIssueDialog" @update:visible="showIssueDialog = $event" :deck="deck" />

    <TieredMenu v-if="authStore.isAuthenticated" ref="menu" :model="menuItems" popup />

    <Dialog v-if="showCompletionDialog" v-model:visible="showCompletionDialog" modal header="Rate Difficulty" class="w-full" style="max-width: 40rem" :closable="true">
      <div class="flex flex-col gap-6">
        <div>
          <p class="text-sm text-muted-color mb-2">How difficult did you find <strong>{{ ratingDeckId === deck.deckId ? localiseTitle(deck) : 'this series' }}</strong>?</p>
          <LazyDifficultyRating :deck-id="ratingDeckId" :current-rating="existingRating" @rated="() => {}" />
        </div>

        <template v-if="completionSuggestions.length > 0">
          <Divider />
          <div v-if="completionCurrentPair">
            <p class="text-sm text-muted-color mb-2">Compare with other media you've completed:</p>
            <LazyDifficultyComparison
              :deck-a="completionCurrentPair.deckA"
              :deck-b="completionCurrentPair.deckB"
              :vote-timestamps="completionVoteTimestamps"
              @voted="advanceCompletion"
              @skipped="advanceCompletion"
            />
          </div>
          <div v-else class="flex flex-col items-center gap-3 py-6">
            <i class="pi pi-check-circle text-green-500 text-4xl" />
            <p class="text-sm text-muted-color text-center">
              Thanks for helping refine the difficulties! <br/>
              <NuxtLink to="/ratings" target="_blank" class="text-primary-500 hover:underline font-semibold">
                Compare more media →
              </NuxtLink>
            </p>
          </div>
        </template>

        <div class="flex justify-end items-center pt-2">
          <Button label="Done" severity="secondary" @click="showCompletionDialog = false" />
        </div>
      </div>
    </Dialog>

    <Message v-if="showCalibrationBanner" severity="info" :closable="true" @close="showCalibrationBanner = false" class="mt-2">
      Help refine the difficulties -
      <NuxtLink to="/ratings" class="font-semibold underline" target="_blank">compare more media</NuxtLink>
    </Message>
  </div>
</template>

<style scoped>
  .description-container:not(.expanded) p {
    display: -webkit-box;
    line-clamp: 2;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  /* Ensure text wraps properly on small screens */
  .flex-1 {
    min-width: 0;
  }

  @media (max-width: 768px) {
    .description-container:not(.expanded) p {
      line-clamp: 4;
      -webkit-line-clamp: 4;
    }
  }

  .description-container.expanded p {
    white-space: pre-line;
  }

  /* Add additional responsive behavior for small screens */
  @media (max-width: 640px) {
    .flex-1 > div > div {
      width: 100%;
    }
  }

  /* Style for stat rows */
  .stat-row {
    padding: 0.2rem;
    border-radius: 3px;
    transition: background-color 0.2s;
  }

  .stat-row:hover {
    background-color: rgba(183, 135, 243, 0.21);
  }

  :deep(.dark) .stat-row:hover {
    background-color: rgba(255, 255, 255, 0.05);
  }

  .ignore-overlay {
    animation: fadeIn 0.3s ease-in-out;
  }

  @keyframes fadeIn {
    from {
      opacity: 0;
    }
    to {
      opacity: 1;
    }
  }
</style>
