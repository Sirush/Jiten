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
    // Set by list views for below-the-fold rows; lets the browser skip
    // layout/paint until the row nears the viewport.
    lazyRender?: boolean;
  }>();

  const showDownloadDialog = ref(false);
  const difficultyRef = ref<{ tooltip: string }>();

  const isAudioVisual = computed(() => [MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio, MediaType.YouTube].includes(props.deck.mediaType));

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

  const showCoverageStrip = computed(
    () => authStore.isAuthenticated && !store.hideCoverageBorders && (props.deck.coverage != 0 || props.deck.uniqueCoverage != 0)
  );
</script>

<template>
  <div class="relative" :class="lazyRender ? '[content-visibility:auto] [contain-intrinsic-size:auto_4rem]' : ''">
    <Card :pt="{ body: { style: 'padding: 0.5rem' } }">
      <template #content>
        <div class="flex flex-row flex-wrap items-center gap-y-2">
          <!-- Title and Media Type -->
          <div class="flex-grow min-w-0 basis-full sm:basis-auto">
            <div class="font-bold truncate max-w-100" :title="localiseTitle(deck)">{{ localiseTitle(deck) }}</div>
            <div class="text-xs text-gray-500 dark:text-gray-400">{{ getMediaTypeText(deck.mediaType) }}</div>
          </div>

          <!-- Key Stats -->
          <div class="flex flex-wrap gap-3 mx-0 sm:mx-3">
            <div v-if="isAudioVisual && deck.speechDuration > 0" class="flex flex-col items-center w-20">
              <div class="text-xs text-gray-600 dark:text-gray-300">Duration</div>
              <div class="font-medium tabular-nums">{{ formattedSpeechDuration }}</div>
            </div>
            <div v-else class="flex flex-col items-center w-20">
              <div class="text-xs text-gray-600 dark:text-gray-300">Characters</div>
              <div class="font-medium tabular-nums">{{ deck.characterCount.toLocaleString() }}</div>
            </div>

            <div class="flex flex-col items-center w-18">
              <div class="text-xs text-gray-600 dark:text-gray-300">Words</div>
              <div class="font-medium tabular-nums">{{ deck.wordCount.toLocaleString() }}</div>
            </div>

            <div class="flex flex-col items-center w-22">
              <div class="text-xs text-gray-600 dark:text-gray-300">Uniq Words</div>
              <div class="font-medium tabular-nums">{{ deck.uniqueWordCount.toLocaleString() }}</div>
            </div>

            <div class="flex flex-col items-center w-14">
              <div class="text-xs text-gray-600 dark:text-gray-300">Kanji</div>
              <div class="font-medium tabular-nums">{{ deck.uniqueKanjiCount.toLocaleString() }}</div>
            </div>

            <div class="flex flex-col items-center w-22" :class="{ invisible: deck.averageSentenceLength === 0 || deck.hideAverageSentenceLength }">
              <div class="text-xs text-gray-600 dark:text-gray-300">Avg sentence</div>
              <div class="font-medium tabular-nums">{{ deck.averageSentenceLength.toFixed(1) }}</div>
            </div>

            <div class="flex flex-col items-center w-22" :class="{ invisible: !deck.popularityRank && !deck.isTrending }">
              <div class="text-xs text-gray-600 dark:text-gray-300">Popularity</div>
              <div class="flex items-center gap-1 font-medium tabular-nums">
                <span v-if="deck.popularityRank">#{{ deck.popularityRank }}</span>
                <Tooltip v-if="deck.isTrending" content="Trending: well above its usual activity this week">
                  <i class="pi pi-arrow-up-right text-xs text-purple-700 dark:text-purple-200" />
                </Tooltip>
              </div>
            </div>

            <div class="flex flex-col items-center w-22" :class="{ invisible: deck.difficulty == -1 }">
              <Tooltip :content="difficultyRef?.tooltip ?? ''">
                <div class="text-xs text-gray-600 dark:text-gray-300">Difficulty</div>
                <DifficultyDisplay
                  ref="difficultyRef"
                  :difficulty="deck.difficulty"
                  :difficulty-raw="deck.difficultyRaw"
                  :difficulty-algorithmic="deck.difficultyAlgorithmic"
                  :user-adjustment="deck.userAdjustment"
                  :vote-count="deck.distinctVoterCount || 0"
                  :adjustment-confidence="deck.adjustmentConfidence || 0"
                  use-stars
                />
              </Tooltip>
            </div>
          </div>

          <!-- Action Buttons -->
          <div class="flex gap-0.5">
            <Button v-tooltip="'View details'" as="router-link" :to="`/decks/media/${deck.deckId}/detail`" size="small" class="p-button-sm">
              <Icon name="material-symbols:info-outline" size="1.5em" />
            </Button>
            <Button v-tooltip="'View vocabulary'" as="router-link" :to="`/decks/media/${deck.deckId}/vocabulary`" size="small" class="p-button-sm">
              <Icon name="material-symbols:menu-book-outline" size="1.5em" />
            </Button>
            <Button v-tooltip="'Download / Learn'" size="small" class="p-button-sm" @click="showDownloadDialog = true">
              <Icon name="material-symbols:download" size="1.5em" />
            </Button>
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
</template>

<style scoped></style>
