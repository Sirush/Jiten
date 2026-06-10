<script lang="ts">
  import { ref } from 'vue';

  const openOverlayDeckId = ref<number | null>(null);

  // One shared document listener for all tiles (a grid renders ~100 of them),
  // attached while at least one tile is mounted.
  let overlayListenerUsers = 0;
  const closeOverlayOnDocumentClick = () => {
    openOverlayDeckId.value = null;
  };
</script>

<script setup lang="ts">
  import { type Deck, MediaType } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import Card from 'primevue/card';
  import { useAuthStore } from '~/stores/authStore';
  import { useJitenStore } from '~/stores/jitenStore';

  const authStore = useAuthStore();
  const store = useJitenStore();
  const localiseTitle = useLocaliseTitle();

  const props = defineProps<{
    deck: Deck;
    lazyCover?: boolean;
  }>();

  const showDownloadDialog = ref(false);
  const difficultyRef = ref<{ tooltip: string }>();

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

  const showOverlay = computed(() => openOverlayDeckId.value === props.deck.deckId);

  function toggleOverlay(e: Event) {
    if (window.matchMedia('(hover: none)').matches) {
      e.preventDefault();
      openOverlayDeckId.value = showOverlay.value ? null : props.deck.deckId;
    }
  }

  onMounted(() => {
    if (overlayListenerUsers++ === 0) document.addEventListener('click', closeOverlayOnDocumentClick);
  });
  onUnmounted(() => {
    if (--overlayListenerUsers === 0) document.removeEventListener('click', closeOverlayOnDocumentClick);
  });

  const borderColor = computed(() => {
    if (!authStore.isAuthenticated || store.hideCoverageBorders || (props.deck.coverage == 0 && props.deck.uniqueCoverage == 0))
      return 'none';
    return getCoverageBorder(props.deck.coverage, '4px');
  });
</script>

<template>
  <!-- content-visibility lets the browser skip layout/paint for offscreen tiles;
       the tile is fixed-size so the intrinsic size is exact (w-34 x h-48). -->
  <div class="relative group h-48 w-34 [content-visibility:auto] [contain-intrinsic-size:8.5rem_12rem]">
    <div class="h-48 w-34 overflow-hidden rounded-md border hover:shadow-md transition-shadow duration-200"   :style="{ 'border': borderColor }">
      <div class="relative h-full" @click.stop="toggleOverlay">
        <!-- Cover image -->
        <img
          :src="deck.coverName == 'nocover.jpg' ? '/img/nocover.jpg' : deck.coverName"
          :alt="deck.originalTitle"
          class="w-full h-full object-cover"
          :loading="lazyCover ? 'lazy' : 'eager'"
          decoding="async"
          width="136"
          height="192"
        />

        <!-- Title overlay at bottom -->
        <div class="absolute flex justify-between items-center bottom-0 left-0 right-0 bg-black/75 0 p-1 text-white">
<!--          <div class="font-bold text-sm truncate">{{ localiseTitle(deck) }}</div>-->
          <div class="text-xs text-gray-300">{{ getMediaTypeText(deck.mediaType) }}</div>
          <div v-if="deck.selectedWordOccurrences != 0" class="bg-purple-500 dark:bg-purple-300 border-1 border-purple-200 dark:border-purple-800 text-white dark:text-black px-2 py-1 rounded-full text-xs font-bold">
            x{{ deck.selectedWordOccurrences.toLocaleString() }}
          </div>
        </div>

        <!-- Hover overlay with additional info -->
        <div
          class="absolute inset-0 bg-black bg-opacity-80 text-white p-2 flex flex-col transition-opacity duration-200"
          :class="showOverlay ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'"
        >
          <div class="font-bold mb-2 truncate">{{ localiseTitle(deck) }}</div>
          <div class="text-xs mb-1">{{ getMediaTypeText(deck.mediaType) }}</div>

          <div class="text-xs space-y-1 mt-auto">
            <div v-if="isAudioVisual && deck.speechDuration > 0" class="flex justify-between">
              <span>Durat.:</span>
              <span class="tabular-nums">{{ formattedSpeechDuration }}</span>
            </div>
            <div v-else class="flex justify-between">
              <span>Chars:</span>
              <span class="tabular-nums">{{ deck.characterCount.toLocaleString() }}</span>
            </div>
            <div class="flex justify-between">
              <span>Uniq words:</span>
              <span class="tabular-nums">{{ deck.uniqueWordCount.toLocaleString() }}</span>
            </div>
            <div v-if="deck.difficulty != -1" class="flex justify-between">
              <span>Difficulty:</span>
              <Tooltip :content="difficultyRef?.tooltip ?? ''">
                <DifficultyDisplay ref="difficultyRef" :difficulty="deck.difficulty" :difficulty-raw="deck.difficultyRaw" :difficulty-algorithmic="deck.difficultyAlgorithmic" :user-adjustment="deck.userAdjustment" :vote-count="deck.distinctVoterCount || 0" use-stars />
              </Tooltip>
            </div>
          </div>

          <div class="mt-2 flex gap-1">
            <Button
              v-tooltip="'View details'"
              as="router-link"
              :to="`/decks/media/${deck.deckId}/detail`"
              size="small"
              class="p-button-sm"
            >
              <Icon name="material-symbols:info-outline" size="1.5em" />
            </Button>
            <Button
              v-tooltip="'View vocabulary'"
              as="router-link"
              :to="`/decks/media/${deck.deckId}/vocabulary`"
              size="small"
              class="p-button-sm"
            >
              <Icon name="material-symbols:menu-book-outline" size="1.5em" />
            </Button>
            <Button v-tooltip="'Download / Learn'" size="small" class="p-button-sm" @click="showDownloadDialog = true">
              <Icon name="material-symbols:download" size="1.5em" />
            </Button>
          </div>
        </div>
      </div>
    </div>
  </div>

  <LazyMediaDeckDownloadDialog v-if="showDownloadDialog" :deck="deck" :visible="showDownloadDialog" @update:visible="showDownloadDialog = $event" />
</template>

<style scoped></style>
