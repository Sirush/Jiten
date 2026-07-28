<script setup lang="ts">
  import type { CardLayoutBlock, HeadwordBlockOptions } from '~/types';
  import { headwordDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<HeadwordBlockOptions>(headwordDefaults, props.block.options));

  const {
    card,
    isFlipped,
    isPreview,
    sample,
    wordData,
    writeInFrontFurigana,
    writeInOutcome,
    headWordTtsText,
    cardAudio,
    customAudioPlaying,
    playCustomAudio,
    showBesideImage,
    imageBlurred,
    cardImageUrl,
    onImageError,
    revealImage,
  } = useCardContext();

  const convertToRuby = useConvertToRuby();

  const sizeClass = computed(() => {
    switch (opts.value.size) {
      case 'small':
        return 'text-3xl md:text-4xl';
      case 'large':
        return 'text-5xl md:text-6xl';
      default:
        return 'text-4xl md:text-5xl';
    }
  });

  const isNewCard = computed(() => (isPreview ? sample!.isNew : !!card.value?.isNewCard));

  const showRubyOnFront = computed(() => {
    if (isFlipped.value) return false;
    const wf = writeInFrontFurigana.value;
    if (wf === 'hide') return false;
    if (wf === 'show') return true;
    switch (opts.value.furigana) {
      case 'shown':
        return true;
      case 'newOnly':
        return isNewCard.value;
      default:
        return false;
    }
  });

  const showAudioButton = computed(() => opts.value.showAudioButton && !isPreview);

  const frontPlain = computed(() => (isPreview ? sample!.wordPlain : (card.value?.wordTextPlain ?? '')));
  const frontRubyHtml = computed(() => convertToRuby(isPreview ? sample!.wordRuby : card.value?.wordText || card.value?.wordTextPlain || '', true));
  const backRubyHtml = computed(() =>
    convertToRuby(isPreview ? sample!.wordRuby : wordData.value?.mainReading?.text || card.value?.wordText || card.value?.wordTextPlain || '', true)
  );
</script>

<template>
  <!-- Plain text before flip, ruby text after flip. -->
  <div class="mb-2 flex items-center justify-center gap-4 md:grid md:grid-cols-[1fr_auto_1fr]">
    <div class="hidden md:block" aria-hidden="true" />
    <div class="flex items-center gap-3">
      <div v-if="showRubyOnFront" class="text-center font-noto-sans head-word" :class="sizeClass" lang="ja" v-html="frontRubyHtml" />
      <div v-else-if="!isFlipped" class="text-center font-noto-sans" :class="sizeClass" lang="ja">
        {{ frontPlain }}
      </div>
      <div
        v-else
        class="text-center font-noto-sans head-word"
        :class="[sizeClass, { 'writein-correct': writeInOutcome === 'correct', 'writein-wrong': writeInOutcome === 'wrong' }]"
        lang="ja"
        v-html="backRubyHtml"
      />
      <TtsButton v-if="showAudioButton && card" :text="headWordTtsText" :word-id="card.wordId" :reading-index="card.readingIndex" size="md" @click.stop />
      <button
        v-if="cardAudio"
        type="button"
        class="inline-flex items-center justify-center text-surface-400 hover:text-primary-500 transition-colors cursor-pointer"
        :class="{ '!text-primary-500': customAudioPlaying }"
        title="Play custom audio"
        @click.stop="playCustomAudio"
      >
        <i class="pi pi-play-circle text-base" />
      </button>
    </div>
    <div class="hidden min-w-0 md:flex md:items-center">
      <SrsCardImage
        v-if="showBesideImage"
        :url="cardImageUrl"
        :blurred="imageBlurred"
        img-class="max-h-36 w-auto max-w-full rounded-lg object-contain border border-surface-200 dark:border-surface-700"
        @error="onImageError"
        @reveal="revealImage"
      />
    </div>
  </div>
</template>

<style scoped>
  .head-word :deep(rt) {
    font-size: 0.35em !important;
    font-weight: 700;
    color: light-dark(var(--p-surface-700), var(--p-surface-400));
  }

  /* Write-in reveal: tint the furigana reading to echo whether the typed reading matched. */
  .head-word.writein-correct :deep(rt) {
    color: var(--p-green-500);
  }
  .head-word.writein-wrong :deep(rt) {
    color: var(--p-red-500);
  }
</style>
