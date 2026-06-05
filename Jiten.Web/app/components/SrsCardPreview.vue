<script setup lang="ts">
  import type { StudySettingsDto } from '~/types';

  const props = defineProps<{ settings: StudySettingsDto }>();

  const convertToRuby = useConvertToRuby();

  const SAMPLE = {
    isNew: true,
    wordRuby: '事[じ]典[てん]',
    wordPlain: '事典',
    reading: 'じてん',
    pitchAccent: 0,
    frequencyRank: 55048,
    pos: 'n',
    definition: 'encyclopedia',
    confusable: ['ことてん'],
    kanji: [
      { character: '事', strokeCount: 8, meaning: 'matter', jlpt: 3 },
      { character: '典', strokeCount: 8, meaning: 'code', jlpt: 1 },
    ],
    composedOf: [
      { ruby: '事[こと]', def: 'thing, matter' },
      { ruby: '典[てん]', def: 'law code' },
    ],
    usedIn: [
      { ruby: '百[ひゃっ]科[か]事[じ]典[てん]', rank: 46030, def: 'encyclopedia' },
      { ruby: '世[せ]界[かい]大[だい]百[ひゃっ]科[か]事[じ]典[てん]', rank: 242669, def: 'Heibonsha World Encyclopedia' },
    ],
    usedInTotal: 2,
    example: { text: 'わからない言葉は事典で調べる。', word: '事典' },
  };

  const isFlipped = ref(false);
  const showMobilePreview = ref(false);

  const headwordRuby = computed(() => convertToRuby(SAMPLE.wordRuby, true));

  const showFuriganaOnFront = computed(() => props.settings.showFuriganaOnFront && (!props.settings.furiganaOnFrontNewOnly || SAMPLE.isNew));

  const exampleHtml = computed(() => {
    const { text, word } = SAMPLE.example;
    const idx = text.indexOf(word);
    if (idx < 0) return text;
    return text.slice(0, idx) + `<span class="text-primary-500 dark:text-primary-500 font-bold">${word}</span>` + text.slice(idx + word.length);
  });

  // Blur reveal — reset whenever the blur toggle changes so the effect is demonstrable both ways.
  const exampleRevealed = ref(false);
  watch(
    () => props.settings.blurExampleSentence,
    () => (exampleRevealed.value = false)
  );
  const exampleBlurred = computed(() => props.settings.blurExampleSentence && !exampleRevealed.value);
</script>

<template>
  <div>
    <!-- On mobile the preview is gated behind a button; on desktop it is always shown.
         The wrapper carries md:hidden — PrimeVue's .p-button display rule would override it on the Button itself. -->
    <div class="md:hidden mb-3">
      <Button
        type="button"
        severity="secondary"
        class="w-full"
        :icon="showMobilePreview ? 'pi pi-eye-slash' : 'pi pi-eye'"
        :label="showMobilePreview ? 'Hide preview' : 'Show card preview'"
        @click="showMobilePreview = !showMobilePreview"
      />
    </div>
    <div :class="showMobilePreview ? 'block' : 'hidden md:block'">
      <div class="flex items-center justify-between mb-2">
        <span class="text-xs text-surface-500 dark:text-surface-400">Preview — sample card</span>
        <Button
          type="button"
          severity="secondary"
          size="small"
          :icon="isFlipped ? 'pi pi-arrow-up' : 'pi pi-arrow-down'"
          :label="isFlipped ? 'Show front' : 'Flip'"
          @click="isFlipped = !isFlipped"
        />
      </div>

      <div
        class="relative bg-surface-0 dark:bg-transparent rounded-2xl shadow-lg dark:shadow-none border border-surface-200 dark:border-surface-700 p-5 lg:p-7"
      >
        <!-- Top bar: frequency rank (back only) -->
        <div class="flex justify-end items-center min-h-[1.25rem]">
          <div v-if="isFlipped && settings.showFrequencyRank" class="text-xs text-gray-400">#{{ SAMPLE.frequencyRank.toLocaleString() }}</div>
        </div>

        <!-- Front (always visible) -->
        <div class="flex flex-col items-center" :class="{ 'cursor-pointer': !isFlipped }" @click="!isFlipped && (isFlipped = true)">
          <div v-if="settings.showCardStatus" class="text-sm mb-3 uppercase tracking-wider text-surface-400 dark:text-surface-300">
            {{ SAMPLE.isNew ? 'New' : 'Review' }}
          </div>

          <!-- Headword: plain before flip, ruby after flip (or on front when furigana enabled) -->
          <div class="mb-2">
            <div v-if="!isFlipped && showFuriganaOnFront" class="text-4xl lg:text-5xl text-center font-noto-sans head-word" lang="ja" v-html="headwordRuby" />
            <div v-else-if="!isFlipped" class="text-4xl lg:text-5xl text-center font-noto-sans" lang="ja">{{ SAMPLE.wordPlain }}</div>
            <div v-else class="text-4xl lg:text-5xl text-center font-noto-sans head-word" lang="ja" v-html="headwordRuby" />
          </div>

          <!-- Example sentence (front) -->
          <div v-if="settings.exampleSentencePosition === 'Front'" class="mt-4 w-full" @click.stop>
            <blockquote
              class="relative inline-block border-l-4 border-primary-500 pl-5 pr-3 py-3 bg-surface-50 dark:bg-surface-800 rounded-r shadow-sm overflow-hidden w-full"
              :class="{ 'blur-md select-none cursor-pointer': exampleBlurred }"
              @click.stop="exampleRevealed = true"
            >
              <div v-html="exampleHtml" class="text-base leading-relaxed" lang="ja" />
            </blockquote>
          </div>

          <!-- Confusable readings -->
          <div v-if="settings.showConfusableReadings" class="mt-4 flex items-center gap-2 text-sm text-amber-700 dark:text-amber-400">
            <i class="pi pi-exclamation-triangle text-xs shrink-0" />
            <span>
              Do not confuse with:
              <template v-for="(cr, i) in SAMPLE.confusable" :key="i">
                <strong>{{ cr }}</strong
                ><span v-if="i < SAMPLE.confusable.length - 1">,&ensp;</span>
              </template>
            </span>
          </div>

          <div v-if="!isFlipped" class="text-sm text-surface-500 dark:text-surface-300 mt-6">Click to reveal</div>
        </div>

        <!-- Back (shown when flipped) -->
        <div v-if="isFlipped" class="mt-6 pt-6 border-t border-surface-200 dark:border-surface-700">
          <!-- Definition -->
          <div class="mb-4">
            <div class="flex flex-wrap gap-1 mt-2 mb-0.5">
              <span class="pos-badge pos-blue">{{ SAMPLE.pos }}</span>
            </div>
            <div><span class="text-gray-400">1.</span> {{ SAMPLE.definition }}</div>
          </div>

          <!-- Example sentence (back) -->
          <div v-if="settings.exampleSentencePosition === 'Back'" class="mb-4">
            <blockquote
              class="relative inline-block border-l-4 border-primary-500 pl-5 pr-3 py-3 bg-surface-50 dark:bg-surface-800 rounded-r shadow-sm overflow-hidden w-full"
              :class="{ 'blur-md select-none cursor-pointer': exampleBlurred }"
              @click.stop="exampleRevealed = true"
            >
              <div v-html="exampleHtml" class="text-base leading-relaxed" lang="ja" />
            </blockquote>
          </div>

          <!-- Pitch accent -->
          <ClientOnly v-if="settings.showPitchAccent">
            <div class="mb-3">
              <h3 class="text-gray-500 dark:text-gray-300 text-sm mb-2">Pitch accent</h3>
              <div class="flex flex-wrap gap-2">
                <LazyPitchDiagram :reading="SAMPLE.reading" :pitch-accent="SAMPLE.pitchAccent" />
              </div>
            </div>
          </ClientOnly>

          <!-- Kanji breakdown -->
          <div v-if="settings.showKanjiBreakdown" class="mt-2">
            <h3 class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mb-2">Kanji breakdown</h3>
            <div class="flex flex-wrap gap-2">
              <div
                v-for="kanji in SAMPLE.kanji"
                :key="kanji.character"
                class="inline-flex items-center gap-2 px-3 py-2 rounded-lg border border-surface-200 dark:border-surface-700"
              >
                <span class="text-2xl font-medium" lang="ja">{{ kanji.character }}</span>
                <div class="flex flex-col text-xs">
                  <span class="text-surface-600 dark:text-surface-400 text-[10px]">{{ kanji.strokeCount }} strokes</span>
                  <span class="text-surface-700 dark:text-surface-300 text-sm max-w-[10rem] truncate">{{ kanji.meaning }}</span>
                  <span class="text-primary-600 dark:text-primary-400 text-[10px]">JLPT N{{ kanji.jlpt }}</span>
                </div>
              </div>
            </div>
          </div>

          <!-- Word composition -->
          <div v-if="settings.showWordComposition" class="mt-3">
            <h3 class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mb-2">Composed of</h3>
            <div class="flex flex-wrap gap-2">
              <div
                v-for="(comp, i) in SAMPLE.composedOf"
                :key="i"
                class="inline-flex items-center gap-3 px-3 py-2 rounded-lg border border-surface-200 dark:border-surface-700"
              >
                <span class="text-xl font-medium" lang="ja" v-html="convertToRuby(comp.ruby)" />
                <span class="text-surface-600 dark:text-surface-400 text-xs max-w-[14rem] line-clamp-2">{{ comp.def }}</span>
              </div>
            </div>
          </div>

          <!-- Word used in -->
          <div v-if="settings.showWordUsedIn" class="mt-4">
            <h3 class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mb-2">Used in {{ SAMPLE.usedInTotal }} words</h3>
            <div class="flex flex-col gap-y-3">
              <div v-for="(comp, i) in SAMPLE.usedIn" :key="i" class="flex items-start gap-3 py-1 border-b border-surface-200/60 dark:border-surface-700/60">
                <span class="text-lg font-medium self-end" lang="ja" v-html="convertToRuby(comp.ruby)" />
                <div class="flex-1 min-w-0 flex flex-col">
                  <span class="text-[10px] text-surface-500 dark:text-surface-400 leading-none self-end">#{{ comp.rank.toLocaleString() }}</span>
                  <span class="text-surface-600 dark:text-surface-400 text-xs line-clamp-2 mt-0.5">{{ comp.def }}</span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
  .head-word :deep(rt) {
    font-size: 0.35em !important;
    font-weight: 700;
    color: light-dark(var(--p-surface-700), var(--p-surface-400));
  }
  :deep(rt) {
    font-size: 0.55em !important;
  }
</style>
