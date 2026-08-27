<script setup lang="ts">
  import type { CardLayoutBlock, CardMediaDto, ExampleSentenceBlockOptions, StudyCardDto, Word } from '~/types';
  import { useSrsStore } from '~/stores/srsStore';
  import { stripRubyMarkup } from '~/utils/stripRubyMarkup';
  import { displayKeyName } from '~/composables/useStudyKeyboard';
  import { resolveCardLayout } from '~/utils/cardLayout';
  import { buildCardAudioPlan } from '~/utils/cardAudioPlan';
  import { cardBlockRegistry } from '~/components/srs/card-blocks/cardBlockRegistry';
  import { exampleSentenceDefaults, resolveOptions } from '~/components/srs/card-blocks/cardBlockOptions';
  import { provideCardContext, type CardContext } from '~/components/srs/card-blocks/useCardContext';

  const props = defineProps<{
    card: StudyCardDto;
    isFlipped: boolean;
    // Write-in review: this card asks the user to type the answer. During the input phase the card
    // front is not click-to-flip and shows the inline input (when inline placement is used).
    writeInActive?: boolean;
    // Furigana override for the front in write-in mode: 'hide' (reading mode), 'show' (meaning mode
    // with reading shown), or 'default' (honour the user's furigana settings).
    frontFurigana?: 'default' | 'hide' | 'show';
    // The tested dimension for the current write-in card; drives which front blocks are masked during
    // the input phase (see frontBlockMasked).
    writeInMode?: 'srs' | 'reading' | 'meaning' | null;
    // After reveal, tint the headword reading green/red to echo whether the typed reading was correct.
    writeInOutcome?: 'correct' | 'wrong' | null;
  }>();

  const emit = defineEmits<{
    flip: [];
  }>();

  const srsStore = useSrsStore();
  const authStore = useAuthStore();
  const { $api } = useNuxtApp();

  // Write-in input phase: a write-in card that hasn't been revealed yet.
  const inputPhase = computed(() => !!props.writeInActive && !props.isFlipped);

  const layout = computed(() => resolveCardLayout(srsStore.studySettings));
  // The frequency rank is rendered as top-bar chrome (see template), so it is filtered out of the
  // content loops even though the layout model carries it as a block.
  const frontBlocks = computed(() => layout.value.front.filter((b) => b.type !== 'frequencyRank'));
  const backBlocks = computed(() => layout.value.back.filter((b) => b.type !== 'frequencyRank'));
  const frontHasSentence = computed(() => layout.value.front.some((b) => b.type === 'exampleSentence'));

  // The rank is chrome, not a body block: its presence and its onlyAfterFlip option come from the
  // layout, not the legacy toggle, so an advanced-added block still shows.
  const frequencyRankBlock = computed(
    () => layout.value.front.find((b) => b.type === 'frequencyRank') ?? layout.value.back.find((b) => b.type === 'frequencyRank')
  );
  const showFrequencyRankChrome = computed(() => {
    const blk = frequencyRankBlock.value;
    if (!blk) return false;
    return (blk.options?.onlyAfterFlip ?? true) ? props.isFlipped : true;
  });

  // A front block is collapsed to a placeholder chip while its tested dimension is being typed in a
  // write-in card: reading-revealing blocks during reading input, meaning-revealing blocks during
  // meaning input. The headword is exempt — it is the prompt and masks its own furigana instead.
  function frontBlockMasked(block: CardLayoutBlock): boolean {
    if (!inputPhase.value || block.type === 'headword') return false;
    const def = cardBlockRegistry[block.type];
    if (props.writeInMode === 'reading') return def.revealsReading;
    if (props.writeInMode === 'meaning') return def.revealsMeaning;
    return false;
  }

  const wordData = ref<Word | null>(null);
  const wordLoadFailed = ref(false);
  const wordLoading = computed(() => !wordData.value && !wordLoadFailed.value);
  const showMenu = ref(false);

  function onClickOutsideMenu() {
    showMenu.value = false;
  }
  onMounted(() => document.addEventListener('click', onClickOutsideMenu));
  onUnmounted(() => document.removeEventListener('click', onClickOutsideMenu));
  let abortController: AbortController | null = null;

  async function fetchWordData() {
    abortController?.abort();
    const controller = new AbortController();
    abortController = controller;
    wordLoadFailed.value = false;

    try {
      wordData.value = await $api<Word>(`vocabulary/${props.card.wordId}/${props.card.readingIndex}/info`, { signal: controller.signal });
    } catch (error) {
      if ((error as { name?: string })?.name === 'AbortError') return;
      wordLoadFailed.value = true;
    }
  }

  watch(
    () => `${props.card.wordId}-${props.card.readingIndex}`,
    () => {
      wordData.value = null;
      showMenu.value = false;
      fetchWordData();
    },
    { immediate: true }
  );

  onUnmounted(() => abortController?.abort());

  const dictCycler = ref<((direction: 1 | -1) => void) | null>(null);
  function registerDictCycler(fn: ((direction: 1 | -1) => void) | null) {
    dictCycler.value = fn;
  }
  // Dictionary cycling is driven by the study page's rebindable keybinds (dictPrev/dictNext); the
  // definitions block registers its cycler here so this delegation survives the block extraction.
  defineExpose({
    cycleDictionary: (direction: 1 | -1) => dictCycler.value?.(direction),
    replayAudio: () => startAutoAudio(props.isFlipped ? 'flip' : 'front', true),
  });

  const cardExample = computed(() => srsStore.getCardExample(props.card.wordId, props.card.readingIndex));

  const exampleRevealed = ref(false);
  const occExpanded = ref(false);

  const sentenceBlock = computed(
    () => layout.value.front.find((b) => b.type === 'exampleSentence') ?? layout.value.back.find((b) => b.type === 'exampleSentence') ?? null
  );

  const sentenceBlurred = computed(() => {
    const opts = resolveOptions<ExampleSentenceBlockOptions>(exampleSentenceDefaults, sentenceBlock.value?.options);
    return opts.blur && !exampleRevealed.value && !(opts.unblurOnFlip && props.isFlipped);
  });

  // Favourite / inline-edit of the displayed example sentence.
  const editingExample = ref(false);
  const favouriting = ref(false);
  const exampleIsCustom = computed(() => !!cardExample.value?.isCustom);
  // Custom examples encode a negated UserExampleSentenceId in sentenceId (see BuildCustomStudyExample).
  const exampleUserSentenceId = computed(() => (cardExample.value?.isCustom ? -cardExample.value.sentenceId : null));

  function buildMarkedText(ex: NonNullable<typeof cardExample.value>): string {
    if (ex.isCustom && ex.customText) return ex.customText;
    const { text, wordPosition, wordLength } = ex;
    if (wordPosition < 0 || wordLength <= 0 || wordPosition >= text.length) return text;
    const before = text.substring(0, wordPosition);
    const word = text.substring(wordPosition, wordPosition + wordLength);
    const after = text.substring(wordPosition + wordLength);
    return `${before}**${word}**${after}`;
  }

  function buildSource(ex: NonNullable<typeof cardExample.value>): string {
    if (ex.isCustom) return ex.customSource ?? '';
    let source = '';
    if (ex.sourceParent) source += localiseTitle(ex.sourceParent) + ' - ';
    if (ex.sourceDeck) source += localiseTitle(ex.sourceDeck);
    return source;
  }

  const editInitialText = computed(() => (cardExample.value ? buildMarkedText(cardExample.value) : ''));
  const editInitialSource = computed(() => (cardExample.value ? buildSource(cardExample.value) : ''));

  function applyCustomDto(dto: UserExampleSentenceDto) {
    srsStore.setCardExample(props.card.wordId, props.card.readingIndex, {
      sentenceId: -dto.userExampleSentenceId,
      text: dto.text.replace(/\*\*/g, ''),
      wordPosition: 0,
      wordLength: 0,
      isCustom: true,
      customText: dto.text,
      customSource: dto.source,
    });
  }

  async function favouriteExample() {
    const ex = cardExample.value;
    if (!ex || ex.isCustom || favouriting.value) return;
    favouriting.value = true;
    try {
      const dto = await $api<UserExampleSentenceDto>(`user/example-sentences/${props.card.wordId}/${props.card.readingIndex}/favourite`, {
        method: 'POST',
        body: { text: buildMarkedText(ex), source: buildSource(ex) || undefined },
      });
      applyCustomDto(dto);
      toast.add({ severity: 'success', summary: 'Saved as custom sentence', life: 2000 });
    } catch {
      toast.add({ severity: 'error', summary: 'Maximum of 3 custom sentences reached', life: 3000 });
    } finally {
      favouriting.value = false;
    }
  }

  function onExampleSaved(dto: UserExampleSentenceDto) {
    applyCustomDto(dto);
    editingExample.value = false;
  }

  async function onExampleDeleted() {
    editingExample.value = false;
    await srsStore.refreshCardExample(props.card.wordId, props.card.readingIndex);
  }

  watch(
    () => `${props.card.wordId}-${props.card.readingIndex}`,
    () => {
      exampleRevealed.value = false;
      occExpanded.value = false;
      editingExample.value = false;
    }
  );

  const {
    sentences: extraSentences,
    expanded: extraSentencesExpanded,
    canLoadMore: canLoadMoreSentences,
    isLoading: isLoadingMoreSentences,
    loadMore: loadMoreSentences,
    toggle: toggleExtraSentences,
  } = useExtraExampleSentences(() => props.card);

  const headWordTtsText = computed(() => {
    const raw = wordData.value?.mainReading?.text || props.card.wordText || props.card.wordTextPlain;
    return stripRubyMarkup(raw);
  });

  const tts = useTts();

  const cardMedia = useCardMedia();
  const wordAudio = useCardWordAudio();
  const cardMediaEntry = computed(() => cardMedia.get(props.card.wordId, props.card.readingIndex));
  const cardImage = computed(() => cardMediaEntry.value?.image ?? null);
  const cardAudio = computed(() => cardMediaEntry.value?.audio ?? null);

  const hasCardMedia = computed(() => !!cardImage.value || !!cardAudio.value);
  const showMediaEditor = ref(false);
  const imageRetried = ref(false);
  const imageManuallyRevealed = ref(false);

  const cardImageUrl = computed(() => cardImage.value?.url ?? '');
  // The card-image block, if present anywhere, drives image rendering: its list decides the side, its
  // options decide beside/below and blur. An absent block means no image is shown at all.
  const cardImageBlock = computed(
    () => layout.value.front.find((b) => b.type === 'cardImage') ?? layout.value.back.find((b) => b.type === 'cardImage') ?? null
  );
  const imageBesideLayout = computed(() => (cardImageBlock.value?.options?.layout ?? 'beside') === 'beside');
  const imageOnFront = computed(() => layout.value.front.some((b) => b.type === 'cardImage'));
  const imageBlurEnabled = computed(() => cardImageBlock.value?.options?.blur ?? true);

  const imageBlurred = computed(() => imageOnFront.value && imageBlurEnabled.value && !props.isFlipped && !imageManuallyRevealed.value);
  const showBesideImage = computed(() => !!cardImage.value && !!cardImageBlock.value && imageBesideLayout.value && (imageOnFront.value || props.isFlipped));

  function playCustomAudio() {
    wordAudio.playWord({
      wordId: props.card.wordId,
      readingIndex: props.card.readingIndex,
      fallbackText: headWordTtsText.value,
      media: cardAudio.value,
      onExpired: async () => (await cardMedia.refreshOne(props.card.wordId, props.card.readingIndex))?.audio ?? null,
    });
  }

  function playHeadwordAudio() {
    wordAudio.stop();
    tts.speakWord(props.card.wordId, props.card.readingIndex, headWordTtsText.value);
  }

  function playCustomToEnd(media: CardMediaDto) {
    return wordAudio.playCustomToEnd({
      media,
      onExpired: async () => (await cardMedia.refreshOne(props.card.wordId, props.card.readingIndex))?.audio ?? null,
    });
  }

  let audioGeneration = 0;

  function afterCardMediaEntry(timeoutMs = 3000): Promise<void> {
    if (cardMediaEntry.value) return Promise.resolve();
    return new Promise((resolve) => {
      const stopWatch = watch(cardMediaEntry, (entry) => {
        if (entry) {
          stopWatch();
          clearTimeout(timer);
          resolve();
        }
      });
      const timer = setTimeout(() => {
        stopWatch();
        resolve();
      }, timeoutMs);
    });
  }

  // Resolves once the current word audio (TTS or custom clip) has finished. Waits briefly for playback
  // to start first, since server-side TTS fetches its audio before the playing state flips on.
  function afterWordAudio(): Promise<void> {
    return new Promise((resolve) => {
      let started = false;
      const stopWatch = watch(wordAudio.isWordPlaying, (playing) => {
        if (playing) {
          started = true;
        } else if (started) {
          stopWatch();
          clearTimeout(timer);
          resolve();
        }
      });
      const timer = setTimeout(() => {
        if (!started) {
          stopWatch();
          resolve();
        }
      }, 1500);
    });
  }

  function wait(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // `forced` is the manual replay: the autoplay on/off toggles and the front/back position are ignored,
  // but the custom-audio-replaces-* composition rules still decide what the clip stands in for.
  async function startAutoAudio(mode: 'front' | 'flip', forced = false) {
    const generation = ++audioGeneration;
    const current = () => generation === audioGeneration;

    const onFront = mode === 'front';
    if (onFront && props.isFlipped) return;
    await afterCardMediaEntry();
    if (!current()) return;
    if (onFront && props.isFlipped) return;
    const example = cardExample.value;
    const media = cardAudio.value;

    const plan = buildCardAudioPlan(srsStore.studySettings, {
      onFront,
      forced,
      hasClip: !!media?.url,
      hasSentence: !!example?.sentenceId,
      isNewCard: props.card.isNewCard,
      frontHasSentence: frontHasSentence.value,
      sentenceBlurred: sentenceBlurred.value,
    });

    const slots = [...plan.slots];
    for (let i = 0; i < slots.length; i++) {
      if (i > 0) {
        await wait(150);
        if (!current()) return;
      }
      const slot = slots[i];
      if (slot === 'clip') {
        const played = await playCustomToEnd(media!);
        if (!current()) return;
        // A clip that never sounded stands in for nothing, so what it replaced plays after all.
        if (!played) slots.push(...plan.fallback.filter((s) => !slots.includes(s)));
      } else if (slot === 'headword') {
        playHeadwordAudio();
        await afterWordAudio();
        if (!current()) return;
      } else {
        playExample(example!);
      }
    }
  }

  async function onImageError() {
    if (imageRetried.value) return;
    imageRetried.value = true;
    await cardMedia.refreshOne(props.card.wordId, props.card.readingIndex);
  }

  const { hasFeature } = useJitenPlus();
  const canEditMedia = computed(() => hasFeature('card-media'));

  const mediaReadings = computed(() => toMediaReadings(wordData.value?.alternativeReadings));

  const droppedFile = ref<File | null>(null);

  function onCardDragOver(e: DragEvent) {
    if (!authStore.isAuthenticated || !canEditMedia.value) return;
    if (e.dataTransfer?.types?.includes('Files')) e.preventDefault();
  }

  function onCardDrop(e: DragEvent) {
    if (!authStore.isAuthenticated || !canEditMedia.value) return;
    const file = e.dataTransfer?.files?.[0];
    if (!file) return;
    e.preventDefault();
    droppedFile.value = file;
    showMediaEditor.value = true;
  }

  watch(showMediaEditor, (open) => {
    if (!open) droppedFile.value = null;
  });

  watch(
    () => `${props.card.wordId}-${props.card.readingIndex}`,
    () => {
      wordAudio.stop();
      imageRetried.value = false;
      imageManuallyRevealed.value = false;
      showMediaEditor.value = false;
      exampleRevealed.value = false;
      startAutoAudio('front');
    },
    { immediate: true }
  );

  onUnmounted(() => {
    audioGeneration++;
    wordAudio.stop();
  });

  function playExample(ex: NonNullable<typeof cardExample.value>) {
    // Custom sentences are encoded with a negated UserExampleSentenceId (see BuildCustomStudyExample).
    if (ex.isCustom) tts.speakCustomSentence(-ex.sentenceId, ex.text);
    else tts.speakSentence(ex.sentenceId, ex.text);
  }

  // The clip stands in for the sentence text-to-speech, so unblurring must not read the sentence aloud
  // after the clip has already played on flip.
  const customAudioCoversSentence = computed(
    () => !!cardAudio.value?.url && srsStore.studySettings.autoPlayCustomAudio && srsStore.studySettings.customAudioReplacesSentence
  );

  function revealExample() {
    exampleRevealed.value = true;
    const example = cardExample.value;
    if (srsStore.studySettings.autoPlaySentence && example?.sentenceId && !customAudioCoversSentence.value) {
      playExample(example);
    }
  }

  const backRef = ref<HTMLElement | null>(null);
  const answerAnnouncement = ref('');

  watch(
    () => props.isFlipped,
    (flipped) => {
      if (!flipped) {
        answerAnnouncement.value = '';
        return;
      }
      answerAnnouncement.value = 'Answer revealed';
      nextTick(() => backRef.value?.focus({ preventScroll: true }));
      startAutoAudio('flip');
    }
  );

  const context: CardContext = {
    card: computed(() => props.card),
    settings: computed(() => srsStore.studySettings),
    isFlipped: toRef(props, 'isFlipped'),
    isPreview: false,
    sample: null,
    wordData,
    wordLoading,
    wordLoadFailed,
    writeInActive: computed(() => !!props.writeInActive),
    writeInFrontFurigana: computed(() => props.frontFurigana ?? 'default'),
    writeInOutcome: computed(() => props.writeInOutcome ?? null),
    writeInInputPhase: inputPhase,
    cardImage,
    cardImageUrl,
    cardAudio,
    customAudioPlaying: wordAudio.customPlaying,
    imageBlurred,
    showBesideImage,
    imageBesideLayout,
    hasCardMedia,
    canEditCardMedia: computed(() => authStore.isAuthenticated && canEditMedia.value),
    openMediaEditor: () => {
      showMediaEditor.value = true;
    },
    headWordTtsText,
    playCustomAudio,
    onImageError,
    revealImage: () => {
      imageManuallyRevealed.value = true;
    },
    cardExample,
    exampleRevealed,
    revealExample,
    registerDictCycler,
  };
  provideCardContext(context);
</script>

<template>
  <div class="w-full mx-auto">
    <div
      class="relative bg-surface-0 dark:bg-transparent rounded-2xl shadow-lg dark:shadow-none border border-surface-200 dark:border-surface-700 p-6 md:p-8"
      @dragover="onCardDragOver"
      @drop="onCardDrop"
    >
      <!-- Screen-reader announcement when the answer is revealed -->
      <div class="sr-only" role="status" aria-live="polite">{{ answerAnnouncement }}</div>

      <!-- Top bar: frequency rank + menu -->
      <div class="flex justify-end items-center gap-2 min-h-[1.25rem]">
        <div v-if="showFrequencyRankChrome && card.frequencyRank > 0" class="text-xs text-gray-400">#{{ card.frequencyRank.toLocaleString() }}</div>
        <button
          class="text-surface-400 hover:text-surface-600 dark:text-surface-400 dark:hover:text-surface-300 p-1 -mr-1 relative"
          @pointerdown.stop
          @click.stop="showMenu = !showMenu"
        >
          <i class="pi pi-ellipsis-h text-sm" />
        </button>
        <div
          v-if="showMenu"
          class="absolute right-4 top-10 z-10 bg-surface-0 dark:bg-surface-800 border border-surface-200 dark:border-surface-700 rounded-lg shadow-lg py-1 min-w-[160px]"
          @pointerdown.stop
        >
          <NuxtLink
            :to="`/vocabulary/${card.wordId}/${card.readingIndex}`"
            target="_blank"
            class="flex items-center gap-2 px-3 py-2 text-sm hover:bg-surface-100 dark:hover:bg-surface-700 transition-colors"
            @click="showMenu = false"
          >
            <i class="pi pi-external-link text-xs" />
            Open vocabulary page
          </NuxtLink>
          <NuxtLink
            :to="`/vocabulary/${card.wordId}/${card.readingIndex}/reviews`"
            target="_blank"
            class="flex items-center gap-2 px-3 py-2 text-sm hover:bg-surface-100 dark:hover:bg-surface-700 transition-colors"
            @click="showMenu = false"
          >
            <i class="pi pi-history text-xs" />
            Review history
          </NuxtLink>
        </div>
      </div>

      <!-- Front (always visible) -->
      <div
        class="flex flex-col items-center"
        :class="{ 'cursor-pointer': !isFlipped && !inputPhase, 'min-h-[50vh]': !isFlipped }"
        :role="!isFlipped && !inputPhase ? 'button' : undefined"
        :tabindex="!isFlipped && !inputPhase ? 0 : undefined"
        :aria-label="!isFlipped && !inputPhase ? 'Reveal answer' : undefined"
        @click="!isFlipped && !inputPhase && emit('flip')"
      >
        <template v-for="block in frontBlocks" :key="block.id">
          <div
            v-if="frontBlockMasked(block)"
            class="my-2 inline-flex items-center gap-1.5 rounded-full bg-surface-100 px-3 py-1 text-xs text-surface-400 dark:bg-surface-800 dark:text-surface-400"
          >
            <i class="pi pi-eye-slash text-[0.7rem]" />
            Hidden during write-in
          </div>
          <component :is="cardBlockRegistry[block.type].component" v-else :block="block" side="front" />
          <!-- Inline write-in input (sits directly under the word during the input phase) -->
          <div v-if="block.type === 'headword' && inputPhase && $slots.writeInput" class="mt-5 w-full" @click.stop>
            <slot name="writeInput" />
          </div>
        </template>

        <div v-if="!isFlipped && !inputPhase" class="text-sm text-surface-500 dark:text-surface-300 mt-6">
          <span class="md:hidden">Tap to reveal</span>
          <span class="hidden md:inline">Click or press {{ displayKeyName(srsStore.studySettings.keybinds.flipCard) }} to reveal</span>
        </div>
      </div>

      <!-- Back (shown when flipped) -->
      <Transition name="reveal">
        <div
          v-if="isFlipped"
          ref="backRef"
          role="region"
          aria-label="Answer"
          tabindex="-1"
          class="mt-6 pt-6 border-t border-surface-200 dark:border-surface-700 focus:outline-none"
        >
          <template v-for="block in backBlocks" :key="block.id">
            <component :is="cardBlockRegistry[block.type].component" :block="block" side="back" />
          </template>
        </div>
      </Transition>
    </div>

    <Dialog v-model:visible="showMediaEditor" modal header="Card media" :style="{ width: '32rem' }" :breakpoints="{ '640px': '94vw' }">
      <CardMediaEditor :word-id="card.wordId" :reading-index="card.readingIndex" :readings="mediaReadings" :dropped-file="droppedFile" />
    </Dialog>
  </div>
</template>

<style scoped>
  /* Reveal animation for the answer side (enter only, so card advance stays snappy). */
  .reveal-enter-active {
    transition:
      opacity 0.18s ease,
      transform 0.18s ease;
  }
  .reveal-enter-from {
    opacity: 0;
    transform: translateY(-6px);
  }

  @media (prefers-reduced-motion: reduce) {
    .reveal-enter-active {
      transition: none;
    }
    .reveal-enter-from {
      opacity: 1;
      transform: none;
    }
  }
</style>
