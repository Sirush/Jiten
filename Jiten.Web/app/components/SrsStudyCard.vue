<script setup lang="ts">
  import type { ExampleSentence, ExampleSentencesByDifficultyResponse, StudyCardDto, Word } from '~/types';
  import { useSrsStore } from '~/stores/srsStore';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { sanitiseHtml } from '~/utils/sanitiseHtml';
  import { stripRubyMarkup } from '~/utils/stripRubyMarkup';
  import ExampleSentenceEntry from '~/components/ExampleSentenceEntry.vue';
  import { displayKeyName } from '~/composables/useStudyKeyboard';

  const props = defineProps<{
    card: StudyCardDto;
    isFlipped: boolean;
  }>();

  const srsStore = useSrsStore();

  const emit = defineEmits<{
    flip: [];
  }>();

  const { $api } = useNuxtApp();
  const localiseTitle = useLocaliseTitle();
  const convertToRuby = useConvertToRuby();

  const wordData = ref<Word | null>(null);
  const wordLoadFailed = ref(false);
  const wordLoading = computed(() => !wordData.value && !wordLoadFailed.value);
  const showMenu = ref(false);

  function onClickOutsideMenu(e: Event) {
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
      wordData.value = await $api<Word>(
        `vocabulary/${props.card.wordId}/${props.card.readingIndex}/info`,
        { signal: controller.signal },
      );
    } catch (error: any) {
      if (error?.name === 'AbortError') return;
      wordLoadFailed.value = true;
    }
  }

  watch(() => `${props.card.wordId}-${props.card.readingIndex}`, () => {
    wordData.value = null;
    showMenu.value = false;
    fetchWordData();
  }, { immediate: true });

  onUnmounted(() => abortController?.abort());

  const { resolvedGroups } = useDictionaryDefinitions(
    computed(() => wordData.value?.mainReading?.text),
    computed(() => wordData.value?.definitions),
  );

  const currentReadingIndex = computed(() => props.card.readingIndex);

  const pitchReadingText = computed(() => {
    if (wordData.value) return wordData.value.mainReading.text;
    const kanaReading = props.card.readings.find(r => r.formType === 1);
    return kanaReading?.text || props.card.wordTextPlain;
  });

  const pitchAccents = computed(() => {
    const accents = wordData.value?.pitchAccents || props.card.pitchAccents;
    return accents && accents.length > 0 ? accents : null;
  });

  const fallbackDefinitions = computed(() => {
    let previousPos: string | null = null;
    return props.card.definitions.map((def) => {
      const posKey = JSON.stringify(def.partsOfSpeech);
      const showPos = def.partsOfSpeech.length > 0 && posKey !== previousPos;
      previousPos = posKey;
      return { ...def, showPos };
    });
  });

  const cardExample = computed(() => srsStore.getCardExample(props.card.wordId, props.card.readingIndex));

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

  const exampleRevealed = ref(false);
  const occExpanded = ref(false);

  watch(() => `${props.card.wordId}-${props.card.readingIndex}`, () => {
    exampleRevealed.value = false;
    occExpanded.value = false;
  });

  const extraSentences = ref<ExampleSentence[]>([]);
  const extraSentencesExpanded = ref(false);
  const canLoadMoreSentences = ref(true);
  const isLoadingMoreSentences = ref(false);
  const bandSize = 0.5;
  const nextBandMin = ref(0);
  const nextBandMax = ref(bandSize);

  async function loadMoreSentences() {
    isLoadingMoreSentences.value = true;
    const sorting = srsStore.studySettings.exampleSentenceSorting;

    try {
      const alreadyLoaded = extraSentences.value.map(s => s.sourceDeck.deckId);

      if (sorting === 'Random') {
        const results = await $api<ExampleSentence[]>(
          `vocabulary/${props.card.wordId}/${props.card.readingIndex}/random-example-sentences`,
          { method: 'POST', body: alreadyLoaded },
        );

        if (results.length === 0) {
          canLoadMoreSentences.value = false;
          return;
        }

        extraSentences.value.push(...results);
      } else {
        const descending = sorting === 'HardestFirst';
        const results = await $api<ExampleSentencesByDifficultyResponse>(
          `vocabulary/${props.card.wordId}/${props.card.readingIndex}/example-sentences-by-difficulty?minDifficulty=${nextBandMin.value}&maxDifficulty=${nextBandMax.value}&descending=${descending}`,
          { method: 'POST', body: alreadyLoaded },
        );

        if (results.sentences.length > 0) {
          extraSentences.value.push(...results.sentences);
        }

        if (descending) {
          nextBandMax.value = results.searchedBandMin;
          nextBandMin.value = nextBandMax.value - bandSize;
          if (nextBandMax.value <= results.minDifficulty) {
            canLoadMoreSentences.value = false;
          }
        } else {
          nextBandMin.value = results.searchedBandMax;
          nextBandMax.value = nextBandMin.value + bandSize;
          if (nextBandMin.value > results.maxDifficulty) {
            canLoadMoreSentences.value = false;
          }
        }

        if (results.sentences.length === 0 && canLoadMoreSentences.value) {
          return;
        }
      }

      extraSentencesExpanded.value = true;
    } catch {
      canLoadMoreSentences.value = false;
    } finally {
      isLoadingMoreSentences.value = false;
    }
  }

  function toggleExtraSentences() {
    if (extraSentences.value.length === 0) {
      loadMoreSentences();
    } else {
      extraSentencesExpanded.value = !extraSentencesExpanded.value;
    }
  }

  watch(() => `${props.card.wordId}-${props.card.readingIndex}`, () => {
    extraSentences.value = [];
    extraSentencesExpanded.value = false;
    canLoadMoreSentences.value = true;
    const sorting = srsStore.studySettings.exampleSentenceSorting;
    if (sorting === 'HardestFirst') {
      nextBandMin.value = 999;
      nextBandMax.value = 999 + bandSize;
    } else {
      nextBandMin.value = 0;
      nextBandMax.value = bandSize;
    }
  });

  const headWordTtsText = computed(() => {
    const raw = wordData.value?.mainReading?.text || props.card.wordText || props.card.wordTextPlain;
    return stripRubyMarkup(raw);
  });

  const tts = useTts();

  watch(() => `${props.card.wordId}-${props.card.readingIndex}`, () => {
    tts.stop();

    const settings = srsStore.studySettings;
    if (!settings.autoPlayWordOnFront) return;
    if (settings.autoPlayWordOnFrontNewOnly && !props.card.isNewCard) return;
    if (props.isFlipped) return;

    const cardKey = `${props.card.wordId}-${props.card.readingIndex}`;
    tts.speakWord(props.card.wordId, props.card.readingIndex, headWordTtsText.value);

    if (settings.autoPlaySentenceOnFront) {
      const example = cardExample.value;
      if (example?.sentenceId) {
        const unwatch = watch(tts.isAnyPlaying, (playing) => {
          if (!playing) {
            unwatch();
            setTimeout(() => {
              if (`${props.card.wordId}-${props.card.readingIndex}` !== cardKey) return;
              tts.speakSentence(example.sentenceId, example.text);
            }, 150);
          }
        });
      }
    }
  }, { immediate: true });

  onUnmounted(() => tts.stop());

  const sentenceBlurred = computed(() => srsStore.studySettings.blurExampleSentence && !exampleRevealed.value);

  const confusableReadings = computed(() => {
    if (!srsStore.studySettings.showConfusableReadings) return [];
    return props.card.confusableReadings ?? [];
  });

  function revealExample() {
    exampleRevealed.value = true;
    const example = cardExample.value;
    if (srsStore.studySettings.autoPlaySentence && example?.sentenceId) {
      tts.speakSentence(example.sentenceId, example.text);
    }
  }

  const backRef = ref<HTMLElement | null>(null);
  const answerAnnouncement = ref('');

  watch(() => props.isFlipped, (flipped) => {
    if (!flipped) {
      answerAnnouncement.value = '';
      return;
    }
    answerAnnouncement.value = 'Answer revealed';
    nextTick(() => backRef.value?.focus({ preventScroll: true }));
    const playWord = srsStore.studySettings.autoPlayWord;
    const example = cardExample.value;
    const playSentence = srsStore.studySettings.autoPlaySentence && example?.sentenceId && !sentenceBlurred.value;
    const cardKey = `${props.card.wordId}-${props.card.readingIndex}`;

    if (playWord) {
      tts.speakWord(props.card.wordId, props.card.readingIndex, headWordTtsText.value);
      if (playSentence) {
        const unwatch = watch(tts.isAnyPlaying, (playing) => {
          if (!playing) {
            unwatch();
            setTimeout(() => {
              if (`${props.card.wordId}-${props.card.readingIndex}` !== cardKey) return;
              tts.speakSentence(example!.sentenceId, example!.text);
            }, 150);
          }
        });
      }
    } else if (playSentence) {
      tts.speakSentence(example!.sentenceId, example!.text);
    }
  });
</script>

<template>
  <div class="w-full mx-auto">
    <div
      class="relative bg-surface-0 dark:bg-transparent rounded-2xl shadow-lg dark:shadow-none border border-surface-200 dark:border-surface-700 p-6 md:p-8"
    >
      <!-- Screen-reader announcement when the answer is revealed -->
      <div class="sr-only" role="status" aria-live="polite">{{ answerAnnouncement }}</div>

      <!-- Top bar: frequency rank + menu -->
      <div class="flex justify-end items-center gap-2 min-h-[1.25rem]">
        <div v-if="isFlipped && card.frequencyRank > 0 && srsStore.studySettings.showFrequencyRank" class="text-xs text-gray-400">
          #{{ card.frequencyRank.toLocaleString() }}
        </div>
        <button
          class="text-surface-400 hover:text-surface-600 dark:text-surface-500 dark:hover:text-surface-300 p-1 -mr-1 relative"
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
        :class="{ 'cursor-pointer min-h-[50vh]': !isFlipped }"
        :role="!isFlipped ? 'button' : undefined"
        :tabindex="!isFlipped ? 0 : undefined"
        :aria-label="!isFlipped ? 'Reveal answer' : undefined"
        @click="!isFlipped && emit('flip')"
        @keydown.enter="!isFlipped && emit('flip')"
        @keydown.space.prevent="!isFlipped && emit('flip')"
      >
        <div v-if="srsStore.studySettings.showCardStatus" class="text-sm mb-4 uppercase tracking-wider" :class="srsStore.againCardKeys.has(`${card.wordId}-${card.readingIndex}`) ? 'text-red-400 dark:text-red-400' : 'text-surface-400 dark:text-surface-300'">
          {{ srsStore.againCardKeys.has(`${card.wordId}-${card.readingIndex}`) ? 'Again' : card.isNewCard ? 'New' : 'Review' }}
        </div>
        <!-- Plain text before flip, ruby text after flip -->
        <div class="flex items-center justify-center gap-3 mb-2">
          <div
            v-if="!isFlipped && srsStore.studySettings.showFuriganaOnFront && (!srsStore.studySettings.furiganaOnFrontNewOnly || card.isNewCard)"
            class="text-4xl md:text-5xl text-center font-noto-sans head-word"
            lang="ja"
            v-html="convertToRuby(card.wordText || card.wordTextPlain, true)"
          />
          <div v-else-if="!isFlipped" class="text-4xl md:text-5xl text-center font-noto-sans" lang="ja">
            {{ card.wordTextPlain }}
          </div>
          <div
            v-else
            class="text-4xl md:text-5xl text-center font-noto-sans head-word"
            lang="ja"
            v-html="convertToRuby(wordData?.mainReading?.text || card.wordText || card.wordTextPlain, true)"
          />
          <TtsButton :text="headWordTtsText" :word-id="card.wordId" :reading-index="card.readingIndex" size="md" @click.stop />
        </div>
        <!-- Example sentence on front -->
        <div v-if="srsStore.studySettings.exampleSentencePosition === 'Front' && exampleSentenceHtml" class="mt-4 w-full" @click.stop>
          <blockquote
            class="relative inline-block border-l-4 pl-5 pr-3 py-3 bg-surface-50 dark:bg-surface-800 rounded-r shadow-sm overflow-hidden w-full"
            :class="[cardExample?.isCustom ? 'border-yellow-500' : 'border-primary-500', { 'blur-md select-none cursor-pointer': srsStore.studySettings.blurExampleSentence && !exampleRevealed }]"
            @click.stop="revealExample()"
          >
            <div class="flex items-start gap-2">
              <div v-html="exampleSentenceHtml" class="text-base leading-relaxed flex-1" lang="ja" />
              <TtsButton v-if="cardExample" :text="cardExample.text" :sentence-id="cardExample.sentenceId" type="sentence" size="sm" class="mt-0.5 shrink-0" />
            </div>
          </blockquote>
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
              <NuxtLink
                :to="`/decks/media/${cardExample.sourceDeck.deckId}/detail`"
                target="_blank"
                class="hover:underline text-primary-600"
              >
                {{ localiseTitle(cardExample.sourceDeck) }}
              </NuxtLink>
              &nbsp;
              ({{ getMediaTypeText(cardExample.sourceDeck.mediaType) }})
            </div>
          </div>

          <button
            class="text-xs text-gray-400 hover:text-gray-500 dark:text-gray-500 dark:hover:text-gray-400 mt-1 ml-1 flex items-center gap-1 cursor-pointer"
            @pointerdown.stop
            @click="toggleExtraSentences"
          >
            <i :class="extraSentencesExpanded ? 'pi pi-chevron-up' : 'pi pi-plus'" class="text-[0.6rem]" />
            {{ extraSentencesExpanded ? 'Hide extra sentences' : 'See more sentences' }}
          </button>

          <div v-if="extraSentencesExpanded" class="mt-2 space-y-2">
            <ExampleSentenceEntry
              v-for="(sentence, i) in extraSentences"
              :key="i"
              :example-sentence="sentence"
              :show-source="true"
            />
            <div v-if="isLoadingMoreSentences" class="border-l-4 border-surface-300 dark:border-surface-600 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r">
              <div class="h-5 w-3/4 bg-surface-200 dark:bg-surface-700 rounded animate-pulse" />
            </div>
            <button
              v-if="extraSentences.length > 0 && canLoadMoreSentences"
              class="text-xs text-gray-400 hover:text-gray-500 dark:text-gray-500 dark:hover:text-gray-400 ml-1 flex items-center gap-1 cursor-pointer"
              :disabled="isLoadingMoreSentences"
              @pointerdown.stop
              @click="loadMoreSentences"
            >
              <i class="pi pi-plus text-[0.6rem]" />
              Load more
            </button>
          </div>
        </div>

        <!-- Confusable readings -->
        <div v-if="confusableReadings.length" class="mt-4 flex items-center gap-2 text-sm text-amber-700 dark:text-amber-400" @click.stop>
          <i class="pi pi-exclamation-triangle text-xs shrink-0" />
          <span>
            Do not confuse with:
            <template v-for="(cr, i) in confusableReadings" :key="i">
              <strong>{{ cr }}</strong><span v-if="i < confusableReadings.length - 1">,&ensp;</span>
            </template>
          </span>
        </div>

        <div v-if="!isFlipped" class="text-sm text-surface-500 dark:text-surface-300 mt-6">
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
        <!-- Definitions -->
        <div class="mb-4">
          <template v-if="wordData">
            <ClientOnly>
              <VocabularyDictionaryDefinitions
                :resolved-groups="resolvedGroups"
                :is-compact="false"
                :current-reading-index="currentReadingIndex"
                :readings="wordData.alternativeReadings"
              />
              <template #fallback>
                <VocabularyDefinitions
                  :definitions="wordData.definitions"
                  :is-compact="false"
                  :current-reading-index="currentReadingIndex"
                  :readings="wordData.alternativeReadings"
                />
              </template>
            </ClientOnly>
          </template>
          <template v-else>
            <div v-for="def in fallbackDefinitions" :key="def.index">
              <div v-if="def.showPos" class="flex flex-wrap gap-1 mt-2 mb-0.5">
                <Tooltip v-for="pos in def.partsOfSpeech" :key="pos" :content="pos" placement="top">
                  <span
                    class="pos-badge"
                    :class="`pos-${posColorClass(abbreviatePos(pos))}`"
                  >{{ abbreviatePos(pos) }}</span>
                </Tooltip>
              </div>
              <div>
                <span class="text-gray-400">{{ def.index }}.</span> {{ def.meanings.join('; ') }}
              </div>
            </div>
            <div v-if="wordLoading" class="flex items-center gap-1.5 mt-2 text-xs text-gray-400">
              <Icon name="svg-spinners:ring-resize" size="0.875rem" />
              <span>Loading full entry…</span>
            </div>
          </template>
        </div>

        <!-- Example sentence (back) -->
        <div v-if="srsStore.studySettings.exampleSentencePosition === 'Back'" class="mb-4">
          <template v-if="exampleSentenceHtml">
            <blockquote
              class="relative inline-block border-l-4 pl-5 pr-3 py-3 bg-surface-50 dark:bg-surface-800 rounded-r shadow-sm overflow-hidden w-full"
              :class="[cardExample?.isCustom ? 'border-yellow-500' : 'border-primary-500', { 'blur-md select-none cursor-pointer': srsStore.studySettings.blurExampleSentence && !exampleRevealed }]"
              @click.stop="revealExample()"
            >
              <div class="flex items-start gap-2">
                <div v-html="exampleSentenceHtml" class="text-base leading-relaxed flex-1" lang="ja" />
                <TtsButton v-if="cardExample" :text="cardExample.text" :sentence-id="cardExample.sentenceId" type="sentence" size="sm" class="mt-0.5 shrink-0" />
              </div>
            </blockquote>
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
                <NuxtLink
                  :to="`/decks/media/${cardExample.sourceDeck.deckId}/detail`"
                  target="_blank"
                  class="hover:underline text-primary-600"
                >
                  {{ localiseTitle(cardExample.sourceDeck) }}
                </NuxtLink>
                &nbsp;
                ({{ getMediaTypeText(cardExample.sourceDeck.mediaType) }})
              </div>
            </div>
          </template>

          <button
            class="text-xs text-gray-400 hover:text-gray-500 dark:text-gray-500 dark:hover:text-gray-400 mt-1 ml-1 flex items-center gap-1 cursor-pointer"
            @pointerdown.stop
            @click="toggleExtraSentences"
          >
            <i :class="extraSentencesExpanded ? 'pi pi-chevron-up' : 'pi pi-plus'" class="text-[0.6rem]" />
            {{ extraSentencesExpanded ? 'Hide extra sentences' : 'See more sentences' }}
          </button>

          <div v-if="extraSentencesExpanded" class="mt-2 space-y-2">
            <ExampleSentenceEntry
              v-for="(sentence, i) in extraSentences"
              :key="i"
              :example-sentence="sentence"
              :show-source="true"
            />
            <div v-if="isLoadingMoreSentences" class="border-l-4 border-surface-300 dark:border-surface-600 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r">
              <div class="h-5 w-3/4 bg-surface-200 dark:bg-surface-700 rounded animate-pulse" />
            </div>
            <button
              v-if="extraSentences.length > 0 && canLoadMoreSentences"
              class="text-xs text-gray-400 hover:text-gray-500 dark:text-gray-500 dark:hover:text-gray-400 ml-1 flex items-center gap-1 cursor-pointer"
              :disabled="isLoadingMoreSentences"
              @pointerdown.stop
              @click="loadMoreSentences"
            >
              <i class="pi pi-plus text-[0.6rem]" />
              Load more
            </button>
          </div>
        </div>

        <!-- Pitch accents -->
        <ClientOnly v-if="srsStore.studySettings.showPitchAccent">
          <div v-if="pitchAccents" class="mb-3">
            <h3 class="text-gray-500 dark:text-gray-300 text-sm mb-2">Pitch accent</h3>
            <div class="flex flex-wrap gap-2">
              <LazyPitchDiagram
                v-for="pitch in pitchAccents"
                :key="pitch"
                :reading="pitchReadingText"
                :pitch-accent="pitch"
              />
            </div>
          </div>
        </ClientOnly>

        <KanjiBreakdown v-if="srsStore.studySettings.showKanjiBreakdown" :key="`${card.wordId}-${card.readingIndex}`" :word-id="card.wordId" :reading-index="card.readingIndex" />

        <WordComposition
          v-if="srsStore.studySettings.showWordComposition && wordData?.composedOf?.length"
          :components="wordData.composedOf"
        />

        <WordUsedIn
          v-if="srsStore.studySettings.showWordUsedIn && wordData?.usedInTotal"
          :key="`usedin-${card.wordId}-${card.readingIndex}`"
          :word-id="card.wordId"
          :reading-index="card.readingIndex"
          :initial-items="wordData.usedIn ?? []"
          :total="wordData.usedInTotal"
          :highlight="wordData.mainReading.text"
          :collapsed-count="2"
        />

        <!-- Deck occurrences -->
        <div v-if="card.deckOccurrences?.length || card.sourceDeckName" class="mt-4 pt-3 border-t border-surface-200 dark:border-surface-700">
          <div
            class="flex flex-wrap gap-x-3 gap-y-1 text-xs text-surface-400 dark:text-surface-500 overflow-hidden transition-[max-height] duration-200"
            :class="occExpanded ? 'max-h-none' : 'max-h-[3.75rem]'"
          >
            <template v-if="card.deckOccurrences?.length">
              <span v-for="occ in card.deckOccurrences" :key="occ.deckId">
                ×{{ occ.occurrences }}
                <template v-if="occ.parentOriginalTitle">{{ localiseTitle({ originalTitle: occ.parentOriginalTitle, romajiTitle: occ.parentRomajiTitle, englishTitle: occ.parentEnglishTitle }) }} - </template>{{ localiseTitle(occ) }}
              </span>
            </template>
            <span v-else-if="card.sourceDeckName">{{ card.sourceDeckName }}</span>
          </div>
          <button
            v-if="card.deckOccurrences && card.deckOccurrences.length > 3"
            type="button"
            class="mt-1 text-xs text-primary-600 dark:text-primary-400 hover:underline"
            @click="occExpanded = !occExpanded"
          >
            {{ occExpanded ? '- View less' : `+ View all ${card.deckOccurrences.length}` }}
          </button>
        </div>

      </div>
      </Transition>
    </div>
  </div>
</template>

<style scoped>
:deep([data-pc-name="tabs"]),
:deep([data-pc-name="tabpanels"]),
:deep([data-pc-name="tabpanel"]),
:deep([data-pc-name="tablist"]) {
  background: transparent !important;
}
.head-word :deep(rt) {
  font-size: 0.35em !important;
  font-weight: 700;
  color: light-dark(var(--p-surface-700), var(--p-surface-400));
}

/* Reveal animation for the answer side (enter only, so card advance stays snappy). */
.reveal-enter-active {
  transition: opacity 0.18s ease, transform 0.18s ease;
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
