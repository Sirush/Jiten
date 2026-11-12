<script setup lang="ts">
  import type { WordCheckLevel, WordCheckResult, WordCheckStep } from '~/types/wordCheck';
  import type { CheckAnswerOption } from '~/types/check';
  import { WordCheckAnswer } from '~/types/enums';

  const props = defineProps<{
    level?: WordCheckLevel | null;
    previous?: { words: number; at: string; batchId: string | null } | null;
  }>();
  const emit = defineEmits<{ done: [result: WordCheckResult]; started: []; undone: []; changed: []; settled: [settled: boolean] }>();

  const { busy, expired, post, undo: undoRequest } = useCheckRequests();

  const ANSWERS: CheckAnswerOption<WordCheckAnswer>[] = [
    { value: WordCheckAnswer.Know, label: 'I know it', key: '1', primary: true },
    { value: WordCheckAnswer.NotSure, label: 'Not sure', key: '2' },
    { value: WordCheckAnswer.No, label: 'No', key: '3' },
  ];

  const step = ref<WordCheckStep | null>(null);
  const started = ref(false);
  const stopped = ref(false);
  const cursor = ref(0);
  const picked = ref<WordCheckAnswer | null>(null);
  const nextButton = ref<{ $el: HTMLElement } | null>(null);
  const answers = ref<{ id: number; answer: WordCheckAnswer }[]>([]);
  const unknownIds = ref<Set<number>>(new Set());
  const result = ref<WordCheckResult | null>(null);
  const undone = ref(false);
  const showRange = ref(false);

  const phase = computed(() => {
    if (undone.value) return 'undone';
    if (result.value) return 'result';
    if (step.value) return step.value.phase;
    if (stopped.value) return 'stopped';
    return props.previous && !started.value ? 'previous' : 'loading';
  });
  const current = computed(() => step.value?.items[cursor.value] ?? null);
  const isRealWord = computed(() => current.value?.reading != null);
  const position = computed(() => (step.value?.answered ?? 0) + cursor.value + 1);
  const total = computed(() => Math.max(step.value?.estimatedTotal ?? 0, position.value));
  const undoBatchId = computed(() => (phase.value === 'previous' ? props.previous?.batchId : result.value?.batchId) ?? null);

  watch(phase, (value) => emit('settled', ['result', 'previous', 'failed', 'undone', 'stopped'].includes(value)), { immediate: true });

  async function start() {
    started.value = true;
    stopped.value = false;
    result.value = null;
    undone.value = false;
    showRange.value = false;
    step.value = null;
    const first = await post<WordCheckStep>('word-check/start', { level: props.level ?? null }, 'Could not start the word check.');
    if (!first) {
      started.value = false;
      stopped.value = !props.previous;
      return;
    }
    emit('started');
    step.value = first;
    cursor.value = 0;
    picked.value = null;
    answers.value = [];
  }

  function pick(value: WordCheckAnswer) {
    if (!current.value || busy.value) return;
    if (picked.value !== null && !isRealWord.value) return;
    const first = picked.value === null;
    picked.value = value;
    if (first) nextTick(() => nextButton.value?.$el.focus());
  }

  async function next() {
    if (!step.value || !current.value || picked.value === null || busy.value) return;
    answers.value.push({ id: current.value.id, answer: picked.value });
    if (cursor.value < step.value.items.length - 1) {
      cursor.value++;
      picked.value = null;
      return;
    }

    const following = await post<WordCheckStep>('word-check/answer', { sessionId: step.value.sessionId, answers: answers.value }, 'Could not save your answers.');
    if (!following) {
      answers.value.pop();
      return;
    }
    step.value = following;
    cursor.value = 0;
    picked.value = null;
    answers.value = [];
    unknownIds.value = new Set();
    if (following.phase === 'verify' && following.items.length === 0) await confirm();
  }

  async function confirm() {
    if (!step.value) return;
    const final = await post<WordCheckResult>(
      'word-check/confirm',
      { sessionId: step.value.sessionId, unknownIds: [...unknownIds.value] },
      'Could not finish the word check.'
    );
    if (!final) return;
    result.value = final;
    emit('done', final);
  }

  async function undo() {
    if (!undoBatchId.value) return;
    if (await undoRequest(undoBatchId.value, 'The words from the word check were removed.', 'Could not undo the word check.')) {
      undone.value = true;
      emit('undone');
    }
  }

  watch(expired, (value) => {
    if (!value) return;
    expired.value = false;
    step.value = null;
    stopped.value = true;
  });

  useCheckKeyboard(
    () => phase.value === 'probe',
    () => ({
      ...Object.fromEntries(ANSWERS.map((a) => [a.key, () => pick(a.value)])),
      ...(picked.value === null ? {} : { ' ': next, Enter: next }),
    })
  );

  onMounted(() => {
    if (!props.previous) start();
  });
</script>

<template>
  <div class="flex flex-col gap-6">
    <section
      v-if="phase === 'loading' || phase === 'probe'"
      class="mx-auto w-full max-w-xl rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-5 md:p-8 flex flex-col gap-6"
    >
      <CheckProgress :position="position" :total="total" :ready="!!current" />

      <div class="flex flex-col items-center gap-3 py-4 min-h-32 justify-center">
        <p class="text-sm text-gray-600 dark:text-gray-300">Do you know this word?</p>
        <p v-if="current" :key="current.id" lang="ja" class="text-4xl md:text-5xl font-medium tracking-wide leading-tight">
          {{ current.text }}
        </p>
        <Skeleton v-else width="12rem" height="3rem" />
      </div>

      <div aria-live="polite" class="min-h-36 flex flex-col items-center justify-center gap-2 border-t border-gray-200 dark:border-neutral-700 pt-4">
        <template v-if="current && picked !== null && isRealWord">
          <p lang="ja" class="text-xl text-gray-700 dark:text-gray-200">{{ current.reading }}</p>
          <ol class="self-stretch list-decimal pl-5 flex flex-col gap-1 text-gray-800 dark:text-gray-200">
            <li v-for="(meaning, i) in current.meanings" :key="i">{{ meaning }}</li>
          </ol>
        </template>
        <p v-else-if="current && picked !== null" class="text-center text-gray-700 dark:text-gray-300">
          Not a real word. A few are mixed in to catch guessing.
        </p>
        <p v-else class="text-center text-sm text-gray-600 dark:text-gray-300">You know a word if you are able to say what it means.</p>
      </div>

      <div class="min-h-48 sm:min-h-14">
        <CheckAnswerButtons v-if="picked === null" :options="ANSWERS" :disabled="!current || busy" @answer="pick" />
        <div v-else class="flex flex-col sm:flex-row gap-3">
          <div v-if="isRealWord" role="radiogroup" aria-label="Your answer" class="flex-1 grid grid-cols-3 gap-1.5">
            <button
              v-for="option in ANSWERS"
              :key="option.key"
              type="button"
              role="radio"
              :aria-checked="picked === option.value"
              :disabled="busy"
              class="min-h-11 sm:min-h-14 rounded-lg border text-sm font-semibold transition-colors focus-visible:outline-2 focus-visible:outline-primary-500 flex items-center justify-center gap-1"
              :class="
                picked === option.value
                  ? 'border-primary-600 ring-1 ring-primary-600 bg-primary-50 text-primary-800 dark:bg-primary-950/40 dark:border-primary-400 dark:ring-primary-400 dark:text-primary-200'
                  : 'border-gray-200 bg-white text-gray-700 hover:border-primary-400 dark:bg-neutral-900 dark:border-neutral-700 dark:text-gray-200'
              "
              @click="pick(option.value)"
            >
              <Icon v-if="picked === option.value" name="material-symbols:check-rounded" size="1.1em" />
              {{ option.label }}
            </button>
          </div>
          <Button ref="nextButton" :loading="busy" class="!min-h-14 !text-base" :class="isRealWord ? 'sm:w-40' : 'flex-1'" @click="next">
            <span class="flex items-center justify-center gap-2 w-full"
              >Next <kbd class="hidden sm:inline-flex items-center px-1.5 rounded-sm text-xs border border-white/40 bg-white/20">Space</kbd></span
            >
          </Button>
        </div>
      </div>
    </section>

    <CheckVerifyStep
      v-else-if="phase === 'verify'"
      v-model="unknownIds"
      :items="step?.items ?? []"
      :busy="busy"
      intro="If your results are correct, you should know most of these. Tap any word you don't know."
      @confirm="confirm"
    />

    <section
      v-else-if="phase === 'failed'"
      class="mx-auto w-full max-w-xl rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-6 flex flex-col gap-4"
    >
      <h2 class="text-xl font-bold">We couldn't get a reliable result</h2>
      <p class="text-gray-700 dark:text-gray-300">
        Your answers didn't fit together well enough to mark words safely, so nothing was marked. You can try again, or pick a range of common words yourself.
      </p>
      <div class="flex flex-wrap gap-3">
        <Button label="Try again" :loading="busy" @click="start" />
        <Button v-if="!showRange" label="Pick a range" severity="secondary" outlined @click="showRange = true" />
      </div>
      <VocabularyImportFrequencyPanel v-if="showRange" @changed="emit('changed')" />
    </section>

    <section
      v-else-if="phase === 'stopped'"
      class="mx-auto w-full max-w-xl rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-6 flex flex-col items-start gap-4"
    >
      <h2 class="text-xl font-bold">The word check stopped</h2>
      <p class="text-gray-700 dark:text-gray-300">Nothing was marked. You can start it again.</p>
      <Button label="Start again" :loading="busy" @click="start" />
    </section>

    <section
      v-else-if="phase === 'previous' && previous"
      class="mx-auto w-full max-w-xl rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-6 md:p-8 flex flex-col items-start gap-2"
    >
      <span
        v-if="previous.words > 0"
        class="w-12 h-12 rounded-full bg-green-100 dark:bg-green-900/40 text-green-700 dark:text-green-300 flex items-center justify-center"
      >
        <Icon name="material-symbols:check-rounded" size="1.75em" />
      </span>
      <p class="text-sm text-gray-600 dark:text-gray-300" :class="{ 'mt-2': previous.words > 0 }">
        You took the word check on {{ formatDateShort(previous.at) }}
      </p>
      <p v-if="previous.words > 0" class="text-4xl md:text-5xl font-bold leading-none tabular-nums">{{ previous.words.toLocaleString() }} words</p>
      <p v-else class="text-xl font-bold">Nothing was marked</p>
      <p class="mt-2 text-gray-700 dark:text-gray-300">Take it again if your Japanese has moved on since. Words you already have stay as they are.</p>
      <div class="flex flex-wrap items-center gap-3 mt-2">
        <Button label="Take it again" severity="secondary" outlined :disabled="busy" @click="start" />
        <Button v-if="previous.batchId" label="Undo" icon="pi pi-undo" text :loading="busy" @click="undo" />
      </div>
    </section>

    <section
      v-else-if="phase === 'undone' || phase === 'result'"
      class="mx-auto w-full max-w-xl rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-6 md:p-8 flex flex-col items-start gap-2"
    >
      <span class="w-12 h-12 rounded-full bg-green-100 dark:bg-green-900/40 text-green-700 dark:text-green-300 flex items-center justify-center">
        <Icon name="material-symbols:check-rounded" size="1.75em" />
      </span>
      <template v-if="phase === 'undone'">
        <p class="mt-2 text-xl font-bold">Undone</p>
        <p class="text-gray-700 dark:text-gray-300">The words from this check were removed. You can take it again anytime.</p>
        <Button label="Take it again" severity="secondary" outlined class="mt-2" @click="start" />
      </template>
      <template v-else-if="result && result.wordsKnown > 0">
        <p class="mt-2 text-sm text-gray-600 dark:text-gray-300">The common words you know</p>
        <p class="text-4xl md:text-5xl font-bold leading-none tabular-nums">{{ result.wordsKnown.toLocaleString() }} words</p>
        <p v-if="result.wordsMarked < result.wordsKnown" class="text-sm text-gray-600 dark:text-gray-300">
          {{
            result.wordsMarked === 0
              ? 'You had all of them marked already.'
              : `${result.wordsMarked.toLocaleString()} of them were new. You had the rest marked already.`
          }}
        </p>
        <p class="mt-2 text-gray-700 dark:text-gray-300">
          This is an estimate. You can reset any single word from its page, or undo the whole check anytime in Settings.
        </p>
        <Button v-if="result.batchId" label="Undo" icon="pi pi-undo" text :loading="busy" class="-ml-3" @click="undo" />
      </template>
      <template v-else>
        <p class="mt-2 text-xl font-bold">Nothing marked yet</p>
        <p class="text-gray-700 dark:text-gray-300">
          You didn't know enough of the common words for Jiten to mark them safely. Learning the most common words first is the fastest way in.
        </p>
      </template>
    </section>
  </div>
</template>
