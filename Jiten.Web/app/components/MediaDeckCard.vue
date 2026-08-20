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
  import { useConfirm } from 'primevue/useconfirm';

  const props = defineProps<{
    deck: Deck;
    isCompact?: boolean;
    hideControl?: boolean;
    hideDetailButton?: boolean;
    titleTag?: string;
    // Set by list views for below-the-fold cards so their covers don't compete
    // with the LCP image. Defaults to eager (single-card pages).
    lazyCover?: boolean;
    // Guest homepage demo: shows the coverage bars without an authenticated user; hides rating and download to keep the card short.
    demoCoverage?: boolean;
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
  const localiseTitle = useLocaliseTitle();
  const confirm = useConfirm();

  const displayAdminFunctions = computed(() => store.displayAdminFunctions);
  const readingSpeed = computed(() => store.readingSpeed);
  const readingDuration = computed(() => Math.round(props.deck.characterCount / readingSpeed.value));
  const speechSpeed = computed(() => props.deck.speechSpeed ?? 0);

  const isAudioVisual = computed(() => [MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio].includes(props.deck.mediaType));

  const hasChildren = computed(() => props.deck.childrenDeckCount > 0);
  const childrenLabel = computed(() => getChildrenCountText(props.deck.mediaType));
  const showChildrenLink = computed(() => hasChildren.value && !props.hideDetailButton);

  // Descriptive text used when sharing the deck to social platforms / the native share sheet.
  const shareTitle = computed(() => `${localiseTitle(props.deck)} — Japanese vocabulary list, stats & Anki deck · Jiten`);

  // Title variants not already shown as the heading, deduped. `ja` marks the original (Japanese)
  // title so it can be wrapped in lang="ja". Aliases are surfaced via JSON-LD alternateName, not here.
  const alternateTitles = computed<{ text: string; ja: boolean }[]>(() => {
    const d = props.deck;
    const shown = localiseTitle(d);
    const list: { text: string; ja: boolean }[] = [];
    const push = (text: string | undefined | null, ja: boolean) => {
      if (text && text !== shown && !list.some((e) => e.text === text)) list.push({ text, ja });
    };
    push(d.originalTitle, true);
    push(d.romajiTitle, false);
    push(d.englishTitle, false);
    return list;
  });

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

  // Menu is mounted lazily on first open — lists render many cards and most
  // menus are never opened.
  const menuActivated = ref(false);

  const toggleMenu = async (event: Event) => {
    menuActivated.value = true;
    await nextTick();
    menu.value?.toggle(event);
  };

  const {
    toggleFavourite,
    toggleIgnore: _toggleIgnore,
    cancelIgnore: _cancelIgnore,
    setStatus,
  } = useDeckPreference(
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
      calibrationTimer = setTimeout(() => {
        showCalibrationBanner.value = false;
      }, 8000);
    }
  });

  const ratingDeckId = ref(props.deck.deckId);

  const openRatingDialog = async (deckId?: number) => {
    ratingDeckId.value = deckId ?? props.deck.deckId;
    existingRating.value = null;
    showCompletionDialog.value = true;
    completionComparisonIndex.value = 0;
    const [rating, suggestions] = await Promise.all([fetchRating(ratingDeckId.value), fetchSuggestions(ratingDeckId.value)]);
    existingRating.value = rating;
    completionSuggestions.value = suggestions
      .slice(0, 2)
      .map((pair) => (pair.deckA.id === ratingDeckId.value ? pair : { deckA: pair.deckB, deckB: pair.deckA }));
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
        rejectLabel: "No, it's still ongoing",
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
    completionComparisonIndex.value < completionSuggestions.value.length ? completionSuggestions.value[completionComparisonIndex.value] : null
  );

  const advanceCompletion = () => {
    completionComparisonIndex.value++;
  };

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
      label: 'Rate difficulty',
      icon: 'pi pi-gauge',
      visible: props.deck.status === DeckStatus.Completed && !props.deck.parentDeckId,
      command: () => {
        openRatingDialog();
      },
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
    {
      label: 'Edit',
      icon: 'pi pi-pencil',
      visible: !props.isCompact && authStore.isAdmin && displayAdminFunctions.value,
      command: () => navigateTo(`/dashboard/media/${props.deck.deckId}`),
    },
    {
      label: 'Report an issue',
      icon: 'pi pi-exclamation-triangle',
      visible: !props.isCompact,
      command: () => {
        showIssueDialog.value = true;
      },
    },
  ]);

  const statusColor = computed(() => {
    if (!props.deck.status || props.deck.status === DeckStatus.None) return '';

    switch (props.deck.status) {
      case DeckStatus.Planning:
        return 'text-gray-500 dark:text-gray-400';
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

  const canEditInline = computed(() => !props.isCompact && authStore.isAdmin && displayAdminFunctions.value);
  const isEditing = ref(false);

  const onMetadataSaved = (result: import('~/types/types').DeckMetadataPatchResult) => {
    emit('update:deck', { ...props.deck, ...result });
    isEditing.value = false;
  };

  const titleBoxRef = ref<HTMLElement | null>(null);
  const isTitleClipped = ref(false);
  let titleResizeObserver: ResizeObserver | undefined;

  const measureTitleClip = () => {
    const el = titleBoxRef.value;
    if (el) isTitleClipped.value = el.scrollHeight > el.clientHeight + 1;
  };

  onMounted(() => {
    if (!props.isCompact) return;
    measureTitleClip();
    if (titleBoxRef.value && typeof ResizeObserver !== 'undefined') {
      titleResizeObserver = new ResizeObserver(measureTitleClip);
      titleResizeObserver.observe(titleBoxRef.value);
    }
    document.fonts?.ready.then(measureTitleClip);
  });

  onBeforeUnmount(() => titleResizeObserver?.disconnect());

  watch(
    () => localiseTitle(props.deck),
    () => nextTick(measureTitleClip)
  );

  const formatOnce = (count: number) => `${count.toLocaleString()} once`;

  const showCoverageStrip = computed(
    () =>
      (authStore.isAuthenticated || props.demoCoverage) &&
      !store.hideCoverageBorders &&
      (props.deck.coverage != 0 || props.deck.uniqueCoverage != 0)
  );
</script>

<template>
  <div class="relative" :class="isCompact ? 'w-80 compact-card' : ''">
    <div
      v-if="showIgnoreOverlay"
      class="absolute inset-0 z-50 flex items-center justify-center backdrop-blur-lg bg-black/50 rounded-lg ignore-overlay"
      @click.stop
    >
      <div class="bg-white dark:bg-gray-800 rounded-lg p-6 max-w-md mx-4 shadow-xl">
        <p class="text-center text-gray-800 dark:text-gray-200 mb-4">This media will be ignored and no longer appear in search results.</p>
        <div class="text-center">
          <button
            type="button"
            class="text-primary-500 hover:text-primary-700 dark:hover:text-primary-400 font-semibold underline-offset-2 hover:underline cursor-pointer"
            @click="cancelIgnore"
          >
            Cancel
          </button>
        </div>
      </div>
    </div>

    <!-- Own positioning context: the calibration banner below would otherwise pull the strip off the card's edge. -->
    <div class="relative" :class="isCompact ? 'h-full' : ''">
    <Card :class="isCompact ? 'h-full' : ''" :pt="{ body: { style: 'padding: 0.75rem 1rem; gap: 0.25rem' } }">
      <template #title>
        <!-- Compact titles are clipped to a fixed two-line box so sibling cards in a row keep
             their stats aligned; the tooltip carries the untruncated title. -->
        <div ref="titleBoxRef" class="overflow-hidden" :class="isCompact ? 'relative leading-snug h-[2.75em]' : ''">
          <div class="float-right flex flex-row items-center gap-1 h-6 shrink-0 ml-2">
            <!-- Matches the icon buttons' p-1.5, so the gap to the first icon equals the gaps between icons. -->
            <div v-if="authStore.isAuthenticated" class="flex items-center gap-2 pr-1.5">
              <i v-if="deck.isFavourite" class="pi pi-star-fill text-yellow-500 text-lg" />
              <i v-if="deck.isIgnored" class="pi pi-eye-slash text-gray-800 dark:text-gray-300 text-lg" />
              <span v-if="deck.status && deck.status !== DeckStatus.None" :class="['text-sm font-bold', statusColor]">
                {{ getDeckStatusText(deck.status) }}
              </span>
            </div>
            <Tooltip v-if="canEditInline" :content="isEditing ? 'Stop editing' : 'Edit metadata inline'">
              <button
                type="button"
                class="p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer"
                :aria-pressed="isEditing"
                @click="isEditing = !isEditing"
              >
                <i class="pi pi-pencil utility-icon" />
              </button>
            </Tooltip>
            <ShareButton v-if="!isCompact" :path="`/decks/media/${deck.deckId}/detail`" :title="shareTitle" />
            <Tooltip content="View stats">
              <router-link
                :to="`/decks/media/${deck.deckId}/stats`"
                class="inline-block p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer"
              >
                <i class="pi pi-chart-bar utility-icon" />
              </router-link>
            </Tooltip>
            <Tooltip v-if="authStore.isAuthenticated" content="More options">
              <button type="button" class="p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer" @click="toggleMenu">
                <i class="pi pi-ellipsis-v utility-icon" />
              </button>
            </Tooltip>
          </div>
          <Tooltip v-if="isCompact" :content="localiseTitle(deck)">
            <component :is="titleTag || 'span'" class="break-words">{{ localiseTitle(deck) }}</component>
          </Tooltip>
          <component :is="titleTag || 'span'" v-else class="break-words">{{ localiseTitle(deck) }}</component>
          <span v-if="isTitleClipped" aria-hidden="true" class="title-clip-ellipsis pointer-events-none absolute bottom-0 right-0 pl-6">…</span>
        </div>
      </template>
      <template v-if="!isCompact" #subtitle>
        <span class="flex items-baseline gap-1 min-w-0 text-xs pl-0.5">
          <span class="font-semibold whitespace-nowrap text-gray-800 dark:text-gray-100">{{ getMediaTypeText(deck.mediaType) }}</span>
          <template v-if="alternateTitles.length && !store.hideAlternativeTitles">
            <span class="text-gray-400 dark:text-gray-400">·</span>
            <!-- Full text stays in the DOM (truncate only clips visually) so it remains crawlable. -->
            <span class="min-w-0 flex-1 truncate md:overflow-visible md:whitespace-normal md:break-words text-gray-600 dark:text-gray-400">
              <template v-for="(t, i) in alternateTitles" :key="i">
                <span v-if="i > 0" class="mx-1 text-gray-400 dark:text-gray-400">·</span>
                <span :lang="t.ja ? 'ja' : undefined">{{ t.text }}</span>
              </template>
            </span>
          </template>
        </span>
      </template>
      <template #content>
        <div class="flex-gap-6" :class="isCompact ? 'h-full flex flex-col' : ''">
          <div class="flex-1 max-w-full overflow-hidden" :class="isCompact ? 'flex flex-col' : ''">
            <div class="flex flex-col md:flex-row md:items-stretch gap-x-4 gap-y-2 w-full" :class="isCompact ? 'flex-1' : ''">
              <div v-if="!isCompact" class="@container text-left text-sm md:w-34 md:shrink-0">
                <div class="flex items-start gap-4 @max-[17rem]:flex-col @max-[17rem]:items-stretch md:block">
                  <div class="shrink-0">
                    <img
                      :src="deck.coverName == 'nocover.jpg' ? '/img/nocover.jpg' : deck.coverName"
                      :alt="localiseTitle(deck)"
                      class="h-48 w-34 min-w-34 object-cover"
                      :fetchpriority="lazyCover ? undefined : 'high'"
                      :loading="lazyCover ? 'lazy' : 'eager'"
                      decoding="async"
                      width="136"
                      height="192"
                    >
                    <Tooltip content="Release date">
                      <div class="mt-2 flex items-center md:justify-center tabular-nums text-gray-600 dark:text-gray-400">
                        {{ formatDateAsYyyyMmDd(new Date(deck.releaseDate)).replace(/-/g, '/') }}
                      </div>
                    </Tooltip>
                  </div>
                  <DeckCoverageBars
                    v-if="(authStore.isAuthenticated || demoCoverage) && (deck.coverage != 0 || deck.uniqueCoverage != 0)"
                    :deck="deck"
                    class="flex-1 min-w-0 @max-[17rem]:flex-none md:mt-3"
                  />
                </div>
              </div>
              <div class="@container min-w-0 flex-1 flex flex-col">
                <div
                  class="grid grid-cols-1 gap-x-3 @xl:gap-x-8 @3xl:gap-x-12 gap-y-1 max-w-[51rem] text-sm"
                  :class="isCompact ? '' : '@xs:grid-cols-2 @3xl:grid-cols-3'"
                >
                  <div class="min-w-0 @max-3xl:contents">
                    <div v-if="isAudioVisual && deck.speechDuration > 0" class="flex justify-between gap-2 stat-row">
                      <Tooltip :content="'Total duration of speech, excluding silence.\nCharacter count: ' + deck.characterCount.toLocaleString()">
                        <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                          <span class="@xl:hidden">Speech time</span><span class="hidden @xl:inline">Speech duration</span>
                        </span>
                      </Tooltip>
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ formattedSpeechDuration }}</span>
                    </div>
                    <div v-else class="flex justify-between gap-2 stat-row">
                      <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                        <span class="@xl:hidden">Characters</span><span class="hidden @xl:inline">Character count</span>
                      </span>
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ deck.characterCount.toLocaleString() }}</span>
                    </div>
                    <div class="flex justify-between gap-2 stat-row">
                      <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">Word count</span>
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ deck.wordCount.toLocaleString() }}</span>
                    </div>
                    <div class="flex justify-between gap-2 stat-row">
                      <Tooltip :content="'Words appearing exactly once: ' + deck.uniqueWordUsedOnceCount.toLocaleString()">
                        <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                          Unique words
                          <span class="hidden @xl:inline text-gray-600 dark:text-gray-400 text-xs tabular-nums"
                            >· {{ formatOnce(deck.uniqueWordUsedOnceCount) }}</span
                          >
                        </span>
                      </Tooltip>
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ deck.uniqueWordCount.toLocaleString() }}</span>
                    </div>
                  </div>

                  <div class="min-w-0 @max-3xl:contents">
                    <div class="flex justify-between gap-2 stat-row">
                      <Tooltip :content="'Kanji appearing exactly once: ' + deck.uniqueKanjiUsedOnceCount.toLocaleString()">
                        <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                          Unique kanji
                          <span class="hidden @xl:inline text-gray-600 dark:text-gray-400 text-xs tabular-nums"
                            >· {{ formatOnce(deck.uniqueKanjiUsedOnceCount) }}</span
                          >
                        </span>
                      </Tooltip>
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ deck.uniqueKanjiCount.toLocaleString() }}</span>
                    </div>
                    <div v-if="deck.averageSentenceLength !== 0 && !deck.hideAverageSentenceLength" class="flex justify-between gap-2 stat-row">
                      <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap"
                        ><span class="@xl:hidden">Avg. sentence</span><span class="hidden @xl:inline">Average sentence length</span></span
                      >
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ deck.averageSentenceLength.toFixed(1) }}</span>
                    </div>
                    <div v-if="speechSpeed > 0" class="flex justify-between gap-2 stat-row">
                      <Tooltip content="Average speed of speech in mora per minute.">
                        <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">Speech speed</span>
                      </Tooltip>
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ speechSpeed.toFixed(0) }}</span>
                    </div>

                    <div v-if="deck.difficulty != -1" class="stat-row cursor-help @max-3xl:col-span-full @max-3xl:order-first">
                      <Tooltip :content="difficultyRef?.tooltip ?? ''" block>
                        <div class="flex justify-between gap-x-2">
                          <span class="text-gray-600 dark:text-gray-400 font-normal shrink-0">
                            Difficulty
                            <i class="pi pi-info-circle text-primary-400 text-xs ml-0.5" />
                          </span>
                          <DifficultyDisplay
                            ref="difficultyRef"
                            :difficulty="deck.difficulty"
                            :difficulty-raw="deck.difficultyRaw"
                            :difficulty-algorithmic="deck.difficultyAlgorithmic"
                            :user-adjustment="deck.userAdjustment"
                            :vote-count="deck.distinctVoterCount || 0"
                            :adjustment-confidence="deck.adjustmentConfidence || 0"
                          />
                        </div>
                      </Tooltip>
                    </div>
                  </div>

                  <div class="min-w-0 @max-3xl:contents">
                    <div
                      v-if="!deck.hideDialoguePercentage && deck.dialoguePercentage != 0 && deck.dialoguePercentage != 100 && !demoCoverage"
                      class="flex justify-between gap-2 stat-row"
                    >
                      <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">Dialogue</span>
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ deck.dialoguePercentage.toFixed(1) }}%</span>
                    </div>

                    <router-link
                      v-if="showChildrenLink"
                      :to="`/decks/media/${deck.deckId}/detail`"
                      class="flex justify-between gap-2 stat-row group cursor-pointer no-underline"
                    >
                      <span class="text-primary-600 dark:text-primary-400 font-normal whitespace-nowrap underline-offset-2 group-hover:underline">{{
                        childrenLabel
                      }}</span>
                      <span class="tabular-nums font-semibold whitespace-nowrap text-primary-600 dark:text-primary-400">
                        {{ deck.childrenDeckCount.toLocaleString() }}
                        <i class="pi pi-arrow-right text-xs ml-0.5 transition-transform group-hover:translate-x-0.5" />
                      </span>
                    </router-link>
                    <div v-else-if="deck.childrenDeckCount != 0" class="flex justify-between gap-2 stat-row">
                      <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">{{ childrenLabel }}</span>
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{
                        deck.childrenDeckCount.toLocaleString()
                      }}</span>
                    </div>

                    <div
                      v-if="
                        (deck.mediaType == MediaType.Novel ||
                          deck.mediaType == MediaType.NonFiction ||
                          deck.mediaType == MediaType.VisualNovel ||
                          deck.mediaType == MediaType.WebNovel) &&
                        !demoCoverage
                      "
                      class="flex justify-between gap-2 stat-row"
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
                        <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                          Duration
                          <i class="pi pi-info-circle cursor-pointer text-primary-500" />
                        </span>
                      </Tooltip>

                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap"
                        >{{ readingDuration > 0 ? readingDuration : '<1' }} h</span
                      >
                    </div>

                    <div v-if="deck.externalRating != 0 && !store.hideExternalRating && !demoCoverage" class="flex justify-between gap-2 stat-row">
                      <Tooltip content="Score based on user ratings from 3rd party websites, such as AniList, TMDB, VNDB or IGDB.">
                        <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                          <span class="@xl:hidden">Rating</span><span class="hidden @xl:inline">External Rating</span>
                        </span>
                      </Tooltip>
                      <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ deck.externalRating }} %</span>
                    </div>

                    <div v-if="deck.selectedWordOccurrences != 0" class="flex justify-between gap-2 stat-row">
                      <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                        <span class="@xl:hidden">Appears</span><span class="hidden @xl:inline">Appears (times)</span>
                      </span>
                      <span class="tabular-nums font-bold whitespace-nowrap">{{ deck.selectedWordOccurrences.toLocaleString() }}</span>
                    </div>
                  </div>
                </div>

                <div class="mt-3">
                  <div v-if="deck.description && !store.hideDescriptions" class="description-container" :class="{ expanded: isDescriptionExpanded }">
                    <p class="whitespace-pre-line mb-0 text-sm leading-relaxed text-gray-600 dark:text-gray-400">{{ deck.description }}</p>
                    <button
                      v-if="deck.description.length > 50"
                      type="button"
                      class="text-primary-500 hover:text-primary-700 text-sm cursor-pointer"
                      @click="toggleDescription"
                    >
                      {{ isDescriptionExpanded ? 'View less' : 'View more' }}
                    </button>
                  </div>
                </div>

                <ExampleSentenceEntry v-if="deck.exampleSentence != undefined" :example-sentence="deck.exampleSentence" />

                <div class="mt-auto">
                  <LazyDeckInlineEditor v-if="isEditing" :deck="deck" @saved="onMetadataSaved" @close="isEditing = false" />

                  <div v-else-if="deck.genres?.length || deck.tags?.length || deck.relationships?.length" class="pt-5 space-y-2 max-w-[51rem]">
                    <GenreTagDisplay v-if="!store.hideGenres && deck.genres?.length" :genres="deck.genres" label="Genres" />
                    <GenreTagDisplay v-if="!store.hideTags && deck.tags?.length" :tags="deck.tags" label="Tags" />
                    <RelatedMediaDisplay v-if="!store.hideRelations && deck.relationships?.length" :relationships="deck.relationships" :deck-id="deck.deckId" />
                  </div>
                </div>
                <DeckCoverageBars
                  v-if="isCompact && (authStore.isAuthenticated || demoCoverage) && (deck.coverage != 0 || deck.uniqueCoverage != 0)"
                  :deck="deck"
                  class="mt-3"
                />
                <div
                  v-if="!hideControl || (!isCompact && sortedLinks.length)"
                  :class="isCompact ? 'pt-4' : 'pt-4 flex flex-col @xl:flex-row @xl:items-end @xl:justify-between gap-x-6 gap-y-3'"
                >
                  <div v-if="!hideControl" class="gap-2" :class="[isCompact ? 'flex flex-row justify-center' : 'grid grid-cols-2 @xl:flex @xl:flex-row']">
                    <Tooltip v-if="!hideDetailButton" content="Details">
                      <Button
                        as="router-link"
                        :to="`/decks/media/${deck.deckId}/detail`"
                        :label="isCompact ? undefined : 'Details'"
                        icon="pi pi-eye"
                        size="small"
                        class="text-center"
                      />
                    </Tooltip>
                    <Tooltip content="Vocabulary">
                      <Button
                        as="router-link"
                        :to="`/decks/media/${deck.deckId}/vocabulary`"
                        :label="isCompact ? undefined : 'Vocabulary'"
                        icon="pi pi-book"
                        size="small"
                        class="text-center"
                      />
                    </Tooltip>
                    <Tooltip v-if="authStore.isAuthenticated" content="Study with SRS">
                      <Button :label="isCompact ? undefined : 'Study'" icon="pi pi-play" size="small" class="text-center" @click="showStudyDeckDialog = true" />
                    </Tooltip>
                    <Tooltip v-if="!demoCoverage" content="Download / Learn">
                      <!-- Label shortens rather than wrapping: a two-line label makes this button taller than its row. -->
                      <Button :icon="isCompact ? 'pi pi-download' : undefined" size="small" class="text-center" @click="showDownloadDialog = true">
                        <template v-if="!isCompact">
                          <i class="pi pi-download" />
                          <span class="whitespace-nowrap">
                            <span class="@xl:hidden">Download</span><span class="hidden @xl:inline">Download / Learn</span>
                          </span>
                        </template>
                      </Button>
                    </Tooltip>
                  </div>

                  <div v-if="!isCompact && sortedLinks.length" class="flex flex-wrap items-center gap-x-3 gap-y-1 @xl:justify-end">
                    <span class="text-xs font-semibold text-gray-600 dark:text-gray-400 uppercase tracking-wider shrink-0">Sources</span>
                    <a v-for="link in sortedLinks" :key="link.url" :href="link.url" target="_blank" class="text-sm">{{ getLinkTypeText(link.linkType) }}</a>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </template>
    </Card>

    <CoverageStrip
      v-if="showCoverageStrip"
      :coverage="deck.coverage"
      :young-coverage="deck.youngCoverage"
      with-tooltip
      class="absolute inset-x-0 bottom-0 z-10 rounded-b-[var(--p-card-border-radius)]"
    />
    </div>

    <LazyMediaDeckDownloadDialog v-if="showDownloadDialog" :deck="deck" :visible="showDownloadDialog" @update:visible="showDownloadDialog = $event" />
    <LazySrsAddDeckDialog v-if="showStudyDeckDialog" :visible="showStudyDeckDialog" :preselected-deck="deck" @update:visible="showStudyDeckDialog = $event" />
    <LazyReportIssueDialog v-if="showIssueDialog" :visible="showIssueDialog" :deck="deck" @update:visible="showIssueDialog = $event" />

    <TieredMenu v-if="authStore.isAuthenticated && menuActivated" ref="menu" :model="menuItems" popup />

    <Dialog
      v-if="showCompletionDialog"
      v-model:visible="showCompletionDialog"
      modal
      header="Rate Difficulty"
      class="w-full"
      style="max-width: 40rem"
      :closable="true"
    >
      <div class="flex flex-col gap-6">
        <div>
          <p class="text-sm text-muted-color mb-2">
            How difficult did you find <strong>{{ ratingDeckId === deck.deckId ? localiseTitle(deck) : 'this series' }}</strong
            >?
          </p>
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
              Thanks for helping refine the difficulties! <br >
              <NuxtLink to="/ratings" target="_blank" class="text-primary-500 hover:underline font-semibold"> Compare more media → </NuxtLink>
            </p>
          </div>
        </template>

        <div class="flex justify-end items-center pt-2">
          <Button label="Done" severity="secondary" @click="showCompletionDialog = false" />
        </div>
      </div>
    </Dialog>

    <Message v-if="showCalibrationBanner" severity="info" :closable="true" class="mt-2" @close="showCalibrationBanner = false">
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

  .title-clip-ellipsis {
    background: linear-gradient(to right, transparent, var(--p-card-background, var(--p-content-background)) 1.5rem);
  }

  .compact-card :deep(.p-card-body) {
    height: 100%;
  }

  .compact-card :deep(.p-card-content) {
    flex: 1 1 auto;
    min-height: 0;
  }

  .stat-row {
    padding: 0.2rem;
    margin-inline: -0.2rem;
    border-radius: var(--radius-sm);
    transition: background-color 0.2s;
  }

  .stat-row > span:last-child {
    flex-shrink: 0;
  }

  .utility-icon {
    color: var(--p-surface-400);
    transition: color 0.15s;
  }

  :is(button, a):hover > .utility-icon,
  :is(button, a):focus-visible > .utility-icon {
    color: var(--p-primary-color);
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
