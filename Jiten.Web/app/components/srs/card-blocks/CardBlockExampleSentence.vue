<script setup lang="ts">
  import type { CardLayoutBlock, ExampleSentenceBlockOptions, UserExampleSentenceDto } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { sanitiseHtml } from '~/utils/sanitiseHtml';
  import ExampleSentenceEntry from '~/components/ExampleSentenceEntry.vue';
  import InlineSentenceEditor from '~/components/InlineSentenceEditor.vue';
  import { useToast } from 'primevue/usetoast';
  import { exampleSentenceDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<ExampleSentenceBlockOptions>(exampleSentenceDefaults, props.block.options));

  const { card, isFlipped, isPreview, sample, cardExample, exampleRevealed, revealExample } = useCardContext();

  const { $api } = useNuxtApp();
  const authStore = useAuthStore();
  const localiseTitle = useLocaliseTitle();
  const srsStore = useSrsStore();
  const toast = useToast();
  const { limits: planLimits } = useJitenPlus();

  const blurred = computed(() => opts.value.blur && !exampleRevealed.value && !(opts.value.unblurOnFlip && isFlipped.value));

  const sizeClass = computed(() => (opts.value.size === 'small' ? 'text-sm' : opts.value.size === 'large' ? 'text-lg' : 'text-base'));

  const previewHtml = computed(() => {
    if (!isPreview) return '';
    const { text, word } = sample!.example;
    const idx = text.indexOf(word);
    if (idx < 0) return text;
    return text.slice(0, idx) + `<span class="text-primary-500 dark:text-primary-500 font-bold">${word}</span>` + text.slice(idx + word.length);
  });

  const exampleSentenceHtml = computed(() => {
    const ex = cardExample.value;
    if (!ex) return null;
    if (ex.isCustom && ex.customText) {
      return parseCustomSentenceHtml(ex.customText);
    }
    const { text, wordPosition, wordLength } = ex;
    if (wordPosition < 0 || wordLength <= 0 || wordPosition >= text.length) {
      return text;
    }
    const before = text.substring(0, wordPosition);
    const word = text.substring(wordPosition, wordPosition + wordLength);
    const after = text.substring(wordPosition + wordLength);
    const html = `${before}<span class="text-primary-500 dark:text-primary-500 font-bold">${word}</span>${after}`;
    return sanitiseHtml(html);
  });

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
    return clampSentenceSource(source);
  }

  const editInitialText = computed(() => (cardExample.value ? buildMarkedText(cardExample.value) : ''));
  const editInitialSource = computed(() => (cardExample.value ? buildSource(cardExample.value) : ''));

  function applyCustomDto(dto: UserExampleSentenceDto) {
    if (!card.value) return;
    srsStore.setCardExample(card.value.wordId, card.value.readingIndex, {
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
    if (!ex || ex.isCustom || favouriting.value || !card.value) return;
    favouriting.value = true;
    try {
      const dto = await $api<UserExampleSentenceDto>(`user/example-sentences/${card.value.wordId}/${card.value.readingIndex}/favourite`, {
        method: 'POST',
        body: { text: buildMarkedText(ex), source: buildSource(ex) || undefined },
      });
      applyCustomDto(dto);
      toast.add({ severity: 'success', summary: 'Saved as custom sentence', life: 2000 });
    } catch {
      toast.add({ severity: 'error', summary: `Maximum of ${planLimits.value.customSentencesPerWord} custom sentences reached`, life: 3000 });
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
    if (card.value) await srsStore.refreshCardExample(card.value.wordId, card.value.readingIndex);
  }

  const {
    sentences: extraSentences,
    expanded: extraSentencesExpanded,
    canLoadMore: canLoadMoreSentences,
    isLoading: isLoadingMoreSentences,
    loadMore: loadMoreSentences,
    toggle: toggleExtraSentences,
  } = useExtraExampleSentences(card);

  watch(
    () => (card.value ? `${card.value.wordId}-${card.value.readingIndex}` : ''),
    () => {
      editingExample.value = false;
    }
  );
</script>

<template>
  <div v-if="isPreview" class="my-4 w-full" @click.stop>
    <blockquote
      class="relative inline-block border-l-4 border-primary-500 pl-5 pr-3 py-3 bg-surface-50 dark:bg-surface-800 rounded-r shadow-sm overflow-hidden w-full"
      :class="{ 'blur-md select-none cursor-pointer': blurred }"
      @click.stop="revealExample()"
    >
      <div class="flex items-start gap-2">
        <div class="leading-relaxed flex-1" :class="sizeClass" lang="ja" v-html="previewHtml" />
        <!-- Inert stand-ins for the real action buttons, so the toggle is demonstrable on sample data. -->
        <div v-if="opts.showActions" class="flex items-center gap-1 mt-0.5 shrink-0">
          <i class="pi pi-volume-up text-sm text-surface-400" />
          <i class="pi pi-star text-sm text-surface-400" />
          <i class="pi pi-pencil text-sm text-surface-400" />
        </div>
      </div>
    </blockquote>
    <div v-if="opts.showSource" class="flex items-center mt-1">
      <span class="text-xs italic mr-2 ml-4">Source:</span>
      <span class="text-xs text-primary-600">{{ sample!.example.source }}</span>
    </div>
  </div>
  <div v-else-if="exampleSentenceHtml || editingExample || side === 'back'" class="my-4 w-full" @click.stop>
    <InlineSentenceEditor
      v-if="editingExample && card"
      :word-id="card.wordId"
      :reading-index="card.readingIndex"
      :initial-text="editInitialText"
      :initial-source="editInitialSource"
      :user-sentence-id="exampleUserSentenceId"
      @saved="onExampleSaved"
      @deleted="onExampleDeleted"
      @cancel="editingExample = false"
    />
    <template v-else>
      <template v-if="exampleSentenceHtml">
        <blockquote
          class="relative inline-block border-l-4 pl-5 pr-3 py-3 bg-surface-50 dark:bg-surface-800 rounded-r shadow-sm overflow-hidden w-full"
          :class="[cardExample?.isCustom ? 'border-yellow-500' : 'border-primary-500', { 'blur-md select-none cursor-pointer': blurred }]"
          @click.stop="revealExample()"
        >
          <div class="flex items-start gap-2">
            <div class="leading-relaxed flex-1" :class="sizeClass" lang="ja" v-html="exampleSentenceHtml" />
            <div v-if="opts.showActions" class="flex items-center gap-1 mt-0.5 shrink-0">
              <TtsButton
                v-if="cardExample"
                :text="cardExample.text"
                :sentence-id="cardExample.isCustom ? undefined : cardExample.sentenceId"
                :custom-sentence-id="cardExample.isCustom ? -cardExample.sentenceId : undefined"
                type="sentence"
                size="sm"
              />
              <button
                v-if="authStore.isAuthenticated && cardExample"
                class="inline-flex items-center justify-center transition-colors"
                :class="exampleIsCustom ? 'text-yellow-500' : 'text-surface-400 hover:text-yellow-500'"
                :disabled="exampleIsCustom || favouriting"
                :title="exampleIsCustom ? 'Saved as custom sentence' : 'Save as custom sentence'"
                @pointerdown.stop
                @click.stop="favouriteExample"
              >
                <i class="pi text-sm" :class="exampleIsCustom ? 'pi-star-fill' : 'pi-star'" />
              </button>
              <button
                v-if="authStore.isAuthenticated && cardExample"
                class="inline-flex items-center justify-center text-surface-400 hover:text-primary-500 transition-colors cursor-pointer"
                title="Edit sentence"
                @pointerdown.stop
                @click.stop="editingExample = true"
              >
                <i class="pi pi-pencil text-sm" />
              </button>
            </div>
          </div>
        </blockquote>
        <template v-if="opts.showSource">
          <div v-if="cardExample?.isCustom && cardExample.customSource" class="flex items-center mt-1">
            <span class="text-xs italic mr-2 ml-4">Source:</span>
            <span class="text-xs">{{ cardExample.customSource }}</span>
          </div>
          <div v-else-if="cardExample?.sourceDeck" class="flex items-center mt-1">
            <span class="text-xs italic mr-2 ml-4">Source:</span>
            <div class="inline-flex items-center text-xs flex-wrap">
              <NuxtLink
                v-if="cardExample.sourceParent"
                :to="`/decks/media/${cardExample.sourceParent.deckId}/detail`"
                target="_blank"
                class="hover:underline text-primary-600"
              >
                {{ localiseTitle(cardExample.sourceParent) }}
              </NuxtLink>
              <span v-if="cardExample.sourceParent" class="mx-1">-</span>
              <NuxtLink :to="`/decks/media/${cardExample.sourceDeck.deckId}/detail`" target="_blank" class="hover:underline text-primary-600">
                {{ localiseTitle(cardExample.sourceDeck) }}
              </NuxtLink>
              &nbsp; ({{ getMediaTypeText(cardExample.sourceDeck.mediaType) }})
            </div>
          </div>
        </template>
      </template>

      <button
        class="text-xs text-gray-400 hover:text-gray-500 dark:text-gray-400 dark:hover:text-gray-400 mt-1 ml-1 flex items-center gap-1 cursor-pointer"
        @pointerdown.stop
        @click="toggleExtraSentences"
      >
        <i :class="extraSentencesExpanded ? 'pi pi-chevron-up' : 'pi pi-plus'" class="text-[0.6rem]" />
        {{ extraSentencesExpanded ? 'Hide extra sentences' : 'See more sentences' }}
      </button>

      <div v-if="extraSentencesExpanded" class="mt-2 space-y-2">
        <ExampleSentenceEntry v-for="(sentence, i) in extraSentences" :key="i" :example-sentence="sentence" :show-source="true" />
        <div v-if="isLoadingMoreSentences" class="border-l-4 border-surface-300 dark:border-surface-600 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r">
          <div class="h-5 w-3/4 bg-surface-200 dark:bg-surface-700 rounded animate-pulse" />
        </div>
        <button
          v-if="extraSentences.length > 0 && canLoadMoreSentences"
          class="text-xs text-gray-400 hover:text-gray-500 dark:text-gray-400 dark:hover:text-gray-400 ml-1 flex items-center gap-1 cursor-pointer"
          :disabled="isLoadingMoreSentences"
          @pointerdown.stop
          @click="loadMoreSentences"
        >
          <i class="pi pi-plus text-[0.6rem]" />
          Load more
        </button>
      </div>
    </template>
  </div>
</template>
