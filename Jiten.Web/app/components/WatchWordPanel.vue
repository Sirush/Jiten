<script setup lang="ts">
  import Button from 'primevue/button';
  import Skeleton from 'primevue/skeleton';
  import { FsrsRating, KnownState, type Word } from '~/types';
  import VocabularyStatus from '~/components/VocabularyStatus.vue';

  const props = defineProps<{
    word: Word | null;
    /** Conjugation chain of the clicked surface form, innermost first */
    conjugation: string[];
    loading: boolean;
    grading: boolean;
    /** The grade this word received moments ago, while its cooldown lasts. */
    lastGrade: FsrsRating | null;
    /** Sentence to mine, with the surface form wrapped in ** markers; empty when the line is unavailable */
    sentence: string;
    sentenceContext: number;
    canExpandSentence: boolean;
    sentenceMined: boolean;
    mining: boolean;
  }>();

  const emit = defineEmits<{
    close: [];
    grade: [rating: FsrsRating];
    changed: [];
    mine: [];
    'update:sentenceContext': [value: number];
  }>();

  const SENTENCE_MAX = 150;
  const sentenceHtml = computed(() => parseCustomSentenceHtml(props.sentence));

  const convertToRuby = useConvertToRuby();

  const { resolvedGroups } = useDictionaryDefinitions(
    computed(() => props.word?.mainReading?.text),
    computed(() => props.word?.definitions)
  );

  const states = computed(() => props.word?.knownStates ?? []);
  const has = (state: KnownState) => states.value.includes(state);
  // Grading a parked, blacklisted or redundant word would silently pull it back into rotation
  const canGrade = computed(
    () => !!props.word && !has(KnownState.Redundant) && !has(KnownState.Blacklisted) && !has(KnownState.Suspended) && !has(KnownState.Mastered)
  );
  const isNew = computed(() => states.value.length === 0 || (states.value.length === 1 && states.value[0] === KnownState.New));

  const grades = [
    { rating: FsrsRating.Again, label: 'Again', key: '1', cls: 'text-red-600 dark:text-red-400 border-red-300 dark:border-red-800 hover:bg-red-50 dark:hover:bg-red-900/20' },
    { rating: FsrsRating.Hard, label: 'Hard', key: '2', cls: 'text-amber-600 dark:text-amber-400 border-amber-300 dark:border-amber-800 hover:bg-amber-50 dark:hover:bg-amber-900/20' },
    { rating: FsrsRating.Good, label: 'Good', key: '3', cls: 'text-green-600 dark:text-green-400 border-green-300 dark:border-green-800 hover:bg-green-50 dark:hover:bg-green-900/20' },
    { rating: FsrsRating.Easy, label: 'Easy', key: '4', cls: 'text-sky-600 dark:text-sky-400 border-sky-300 dark:border-sky-800 hover:bg-sky-50 dark:hover:bg-sky-900/20' },
  ];
  const lastGradeLabel = computed(() => grades.find((g) => g.rating === props.lastGrade)?.label ?? '');
  // Outermost step first reads as the form was built, matching the vocabulary page
  const conjugationText = computed(() => [...props.conjugation].reverse().join(' ; '));

  // Desktop renders in place beside the transcript; phones get a bottom sheet teleported out of the column
  const isDesktop = ref(false);
  onMounted(() => {
    const query = window.matchMedia('(min-width: 1024px)');
    const update = () => (isDesktop.value = query.matches);
    update();
    query.addEventListener('change', update);
    onBeforeUnmount(() => query.removeEventListener('change', update));
  });
</script>

<template>
  <Teleport to="body" :disabled="isDesktop">
    <aside
      class="z-40 flex flex-col rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-xl text-sm fixed inset-x-2 bottom-2 max-h-[55vh] lg:absolute lg:inset-x-auto lg:bottom-auto lg:top-1/2 lg:-translate-y-1/2 lg:max-h-[min(36rem,calc(100vh-2rem))] lg:w-[22rem] lg:left-[min(calc(100%+1.5rem),calc(50vw+5rem))]"
      aria-label="Word"
    >
      <div class="flex items-start justify-between gap-2 p-3 pb-2 border-b border-surface-200 dark:border-surface-700">
        <Skeleton v-if="loading || !word" height="3.5rem" class="flex-1" />
        <div v-else class="min-w-0 flex flex-col gap-1">
          <NuxtLink
            :to="`/vocabulary/${word.wordId}/${word.mainReading.readingIndex}`"
            class="text-3xl leading-tight font-noto-sans !text-surface-900 dark:!text-surface-0 hover:underline"
            lang="ja"
            v-html="convertToRuby(word.mainReading.text)"
          />
          <div v-if="conjugationText" class="text-xs text-surface-500 dark:text-surface-400">Conjugation: {{ conjugationText }}</div>
          <div class="flex flex-wrap items-center gap-x-3 gap-y-1 text-surface-500 dark:text-surface-400">
            <span v-if="word.mainReading.frequencyRank > 0" class="tabular-nums">Rank #{{ word.mainReading.frequencyRank.toLocaleString() }}</span>
            <VocabularyStatus :word="word" @changed="emit('changed')" />
          </div>
        </div>
        <Button icon="pi pi-times" text rounded size="small" severity="secondary" aria-label="Close word" @click="emit('close')" />
      </div>

      <div class="flex-1 min-h-0 overflow-y-auto p-3 flex flex-col gap-3">
        <Skeleton v-if="loading || !word" height="10rem" />
        <template v-else>
          <ClientOnly>
            <div v-if="word.pitchAccents && word.pitchAccents.length > 0" class="flex flex-wrap gap-6">
              <LazyPitchDiagram v-for="pitchAccent in word.pitchAccents" :key="pitchAccent" :reading="word.mainReading.text" :pitch-accent="pitchAccent" />
            </div>
          </ClientOnly>

          <div v-if="lastGrade !== null" class="flex items-center gap-2 rounded border border-surface-200 dark:border-surface-700 px-2 py-1.5 text-surface-600 dark:text-surface-300">
            <i class="pi pi-check text-green-600 dark:text-green-400" />
            <span>Graded {{ lastGradeLabel }}</span>
          </div>
          <div v-else-if="canGrade" class="flex flex-col gap-1">
            <div class="flex gap-1.5">
              <Tooltip v-for="g in grades" :key="g.rating" :content="`Press ${g.key}`">
                <button
                  type="button"
                  class="flex-1 rounded border px-2 py-1 font-medium cursor-pointer disabled:opacity-50 disabled:cursor-default"
                  :class="g.cls"
                  :disabled="grading"
                  @click="emit('grade', g.rating)"
                >
                  {{ g.label }}
                </button>
              </Tooltip>
            </div>
            <span v-if="isNew" class="text-xs text-surface-500 dark:text-surface-400">Grading a new word adds it to your reviews.</span>
          </div>

          <div v-if="sentence" class="flex flex-col gap-1.5 rounded border border-surface-200 dark:border-surface-700 p-2">
            <p class="font-noto-sans leading-relaxed" lang="ja" v-html="sentenceHtml" />
            <div class="flex items-center justify-between gap-2 text-xs text-surface-500 dark:text-surface-400">
              <span class="flex items-center gap-1">
                <Button
                  icon="pi pi-minus"
                  text
                  rounded
                  size="small"
                  severity="secondary"
                  aria-label="Fewer context lines"
                  :disabled="sentenceContext === 0"
                  @click="emit('update:sentenceContext', sentenceContext - 1)"
                />
                <span class="tabular-nums">{{ sentenceContext === 0 ? 'This line' : `±${sentenceContext} ${sentenceContext === 1 ? 'line' : 'lines'}` }}</span>
                <Button
                  icon="pi pi-plus"
                  text
                  rounded
                  size="small"
                  severity="secondary"
                  aria-label="More context lines"
                  :disabled="!canExpandSentence"
                  @click="emit('update:sentenceContext', sentenceContext + 1)"
                />
              </span>
              <span class="tabular-nums" :class="sentence.length >= SENTENCE_MAX ? 'text-amber-600 dark:text-amber-400' : ''">{{ sentence.length }} / {{ SENTENCE_MAX }}</span>
            </div>
            <div v-if="sentenceMined" class="flex items-center gap-2 text-surface-600 dark:text-surface-300">
              <i class="pi pi-check text-green-600 dark:text-green-400" />
              <span>Sentence saved</span>
            </div>
            <Button v-else label="Save as example sentence" icon="pi pi-bookmark" size="small" severity="secondary" outlined :loading="mining" @click="emit('mine')" />
          </div>

          <VocabularyDictionaryDefinitions :resolved-groups="resolvedGroups" :is-compact="false" :current-reading-index="word.mainReading.readingIndex" :readings="word.alternativeReadings" />
        </template>
      </div>
    </aside>
  </Teleport>
</template>
