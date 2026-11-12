<script setup lang="ts">
  import type { ReadingResult, ReadingStep } from '~/types/readingCheck';
  import { DeckDownloadType, DeckOrder, ReadingCheckAnswer, StudyDeckType } from '~/types/enums';
  import type { CheckAnswerOption } from '~/types/check';
  import { useSrsStore } from '~/stores/srsStore';

  const props = defineProps<{ deckIds: number[] }>();
  const emit = defineEmits<{ done: [result: ReadingResult]; cancel: [] }>();

  const toast = useToast();
  const srsStore = useSrsStore();
  const { busy, expired, post } = useCheckRequests();

  const ANSWERS: CheckAnswerOption<ReadingCheckAnswer>[] = [
    {
      value: ReadingCheckAnswer.Knew,
      label: 'Knew it',
      key: '1',
      tone: '!border-green-300 !text-green-700 dark:!text-green-300 dark:!border-green-800',
    },
    {
      value: ReadingCheckAnswer.Partly,
      label: 'Partly',
      key: '2',
      tone: '!border-orange-300 !text-orange-800 dark:!text-orange-300 dark:!border-orange-800',
    },
    {
      value: ReadingCheckAnswer.DidntKnow,
      label: "Didn't know",
      key: '3',
      tone: '!border-red-300 !text-red-700 dark:!text-red-300 dark:!border-red-800',
    },
  ];

  const step = ref<ReadingStep | null>(null);
  const cursor = ref(0);
  const revealed = ref(false);
  const answers = ref<{ id: number; answer: ReadingCheckAnswer }[]>([]);
  const unknownIds = ref<Set<number>>(new Set());
  const result = ref<ReadingResult | null>(null);
  const studyDeckCreated = ref(false);
  let studyDeckId: number | null = null;
  const showAllStudy = ref(false);

  const LEVEL_NAMES: Record<number, string> = { 1: 'Strict', 2: 'Balanced', 3: 'Relaxed', 4: 'Generous', 5: 'Bold' };
  const RECOMMENDED_LEVEL = 2;

  const phase = computed(() => (result.value ? 'result' : (step.value?.phase ?? 'loading')));
  const asking = computed(() => phase.value === 'probe');
  const current = computed(() => (asking.value ? (step.value?.items[cursor.value] ?? null) : null));
  const position = computed(() => (step.value?.answered ?? 0) + cursor.value + 1);
  const total = computed(() => Math.max(step.value?.estimatedTotal ?? 0, position.value));
  const chosenLevel = ref(RECOMMENDED_LEVEL);
  const chosen = computed(() => step.value?.levels.find((l) => l.level === chosenLevel.value) ?? null);
  const levelsDiffer = computed(() => new Set(step.value?.levels.map((l) => l.known)).size > 1);
  const reviewSummary = computed(() => {
    const known = step.value?.known ?? 0;
    return known === 1 ? '1 word will be marked as known.' : `${known.toLocaleString()} words will be marked as known.`;
  });

  function setStep(next: ReadingStep | null) {
    if (!next) return;
    step.value = next;
    if (next.phase === 'fit') chosenLevel.value = next.level;
    cursor.value = 0;
    revealed.value = false;
    answers.value = [];
    unknownIds.value = new Set();
  }

  async function start() {
    const next = await post<ReadingStep>('reading-check/start', { deckIds: props.deckIds });
    if (!next) {
      emit('cancel');
      return;
    }
    setStep(next);
  }

  async function answer(value: ReadingCheckAnswer) {
    if (!step.value || !current.value || !revealed.value || busy.value) return;
    answers.value.push({ id: current.value.id, answer: value });
    if (cursor.value < step.value.items.length - 1) {
      cursor.value++;
      revealed.value = false;
      return;
    }
    const next = await post<ReadingStep>('reading-check/answer', { sessionId: step.value.sessionId, answers: answers.value });
    if (next) setStep(next);
    else answers.value.pop();
  }

  async function verify() {
    if (!step.value) return;
    const next = await post<ReadingStep>('reading-check/verify', { sessionId: step.value.sessionId, level: chosenLevel.value });
    if (!next) return;
    setStep(next);
    if (next.items.length === 0) await confirm();
  }

  async function reviewMore() {
    if (!step.value) return;
    const next = await post<ReadingStep>('reading-check/review', { sessionId: step.value.sessionId, unknownIds: [...unknownIds.value] });
    if (!next) return;
    setStep(next);
    if (next.items.length === 0) await confirm();
  }

  async function confirm() {
    if (!step.value) return;
    const final = await post<ReadingResult>('reading-check/confirm', { sessionId: step.value.sessionId, unknownIds: [...unknownIds.value] });
    if (!final) return;
    result.value = final;
    emit('done', final);
  }

  async function studyThese() {
    if (!result.value || result.value.studyList.length === 0) return;
    busy.value = true;
    try {
      studyDeckId ??= (
        await srsStore.addStudyDeck({
          deckType: StudyDeckType.StaticWordList,
          name: "From what I've read",
          downloadType: DeckDownloadType.Full,
          order: DeckOrder.ImportOrder,
          minFrequency: 0,
          maxFrequency: 0,
          excludeKana: false,
        })
      ).userStudyDeckId;
      await srsStore.addDeckWordsBatch(
        studyDeckId,
        result.value.studyList.map((w) => ({ wordId: w.wordId, readingIndex: w.readingIndex }))
      );
      studyDeckCreated.value = true;
      toast.add({ severity: 'success', detail: `Added a study deck with ${result.value.studyList.length.toLocaleString()} words.`, life: 5000 });
    } catch (error) {
      toast.add({ severity: 'error', detail: extractApiError(error, 'Something went wrong. Try again.'), life: 5000 });
    } finally {
      busy.value = false;
    }
  }

  function reveal() {
    revealed.value = true;
  }

  watch(expired, (value) => {
    if (value && !result.value) emit('cancel');
  });

  useCheckKeyboard(
    () => asking.value && !!current.value,
    () => (revealed.value ? Object.fromEntries(ANSWERS.map((a) => [a.key, () => answer(a.value)])) : { ' ': reveal, Enter: reveal })
  );

  onMounted(start);
</script>

<template>
  <div class="flex flex-col gap-5">
    <section v-if="phase === 'loading' || asking" class="mx-auto w-full max-w-xl flex flex-col gap-4">
      <CheckProgress :position="position" :total="total" :ready="!!current" />

      <div class="rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-6 flex flex-col items-center gap-5 min-h-80">
        <p v-if="current" :key="current.id" lang="ja" class="text-5xl md:text-6xl font-medium pt-6">{{ current.text }}</p>
        <Skeleton v-else width="10rem" height="4rem" class="mt-6" />
        <template v-if="current && revealed">
          <div aria-hidden="true" class="w-full h-px bg-gray-200 dark:bg-neutral-700" />
          <p lang="ja" class="text-2xl text-gray-700 dark:text-gray-200">{{ current.reading }}</p>
          <ol class="self-stretch list-decimal pl-5 flex flex-col gap-1.5 text-gray-800 dark:text-gray-200">
            <li v-for="(meaning, i) in current.meanings" :key="i">{{ meaning }}</li>
          </ol>
          <p class="mt-auto text-sm text-gray-600 dark:text-gray-300">Did you know the reading and the meaning?</p>
        </template>
        <p v-else-if="current" class="mt-auto text-sm text-gray-600 dark:text-gray-300">Try to recall the reading and meaning, then show the answer.</p>
      </div>

      <Button v-if="!revealed" label="Show answer" :disabled="!current || busy" class="!min-h-14" @click="reveal">
        <span class="flex items-center justify-center gap-2 w-full"
          >Show answer <kbd class="hidden sm:inline-flex items-center px-1.5 rounded-sm text-xs border border-white/40 bg-white/20">Space</kbd></span
        >
      </Button>
      <template v-else>
        <CheckAnswerButtons :options="ANSWERS" :disabled="busy" @answer="answer" />
        <p class="text-center text-sm text-gray-600 dark:text-gray-300">Partly: for example, the meaning but not the reading.</p>
      </template>
    </section>

    <section
      v-else-if="phase === 'fit' && step"
      class="rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-5 md:p-6 flex flex-col gap-5"
    >
      <p v-if="!levelsDiffer" class="text-sm text-gray-600 dark:text-gray-300">Based on your {{ step.answered }} answers.</p>
      <div v-else class="flex flex-col gap-2">
        <span class="text-sm text-gray-600 dark:text-gray-300">Based on your {{ step.answered }} answers. How sure should Jiten be before marking a word?</span>
        <div role="radiogroup" aria-label="How sure Jiten should be" class="grid grid-cols-5 gap-1.5">
          <button
            v-for="option in step.levels"
            :key="option.level"
            type="button"
            role="radio"
            :aria-checked="chosenLevel === option.level"
            class="min-h-11 rounded-lg border text-sm font-semibold transition-colors focus-visible:outline-2 focus-visible:outline-primary-500"
            :class="
              chosenLevel === option.level
                ? 'border-primary-600 ring-1 ring-primary-600 bg-primary-50 text-primary-800 dark:bg-primary-950/40 dark:border-primary-400 dark:ring-primary-400 dark:text-primary-200'
                : 'border-gray-200 bg-white text-gray-700 hover:border-primary-400 dark:bg-neutral-900 dark:border-neutral-700 dark:text-gray-200'
            "
            @click="chosenLevel = option.level"
          >
            {{ option.level }}
          </button>
        </div>
        <div class="flex justify-between text-xs text-gray-600 dark:text-gray-300">
          <span>Strict</span>
          <span>Bold</span>
        </div>
      </div>

      <div v-if="chosen" class="flex flex-col gap-1.5" aria-live="polite">
        <span class="text-[11px] font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">
          {{ levelsDiffer ? `${LEVEL_NAMES[chosen.level]}${chosen.level === RECOMMENDED_LEVEL ? ', recommended' : ''}` : 'Your result' }}
        </span>
        <span class="text-4xl font-bold tabular-nums">{{ chosen.known.toLocaleString() }} words</span>
        <span class="text-sm text-gray-700 dark:text-gray-300">marked as known.</span>
        <span v-if="chosen.expectedWrong > 0" class="text-sm text-gray-600 dark:text-gray-300">
          About {{ chosen.expectedWrong.toLocaleString() }} of the known words may be ones you don't know yet.
        </span>
      </div>

      <div class="flex justify-end border-t border-gray-100 dark:border-neutral-800 pt-4">
        <Button label="Continue with this result" :loading="busy" @click="verify" />
      </div>
    </section>

    <CheckVerifyStep
      v-else-if="phase === 'verify' && step"
      v-model="unknownIds"
      :items="step.items"
      :busy="busy"
      :columns="4"
      title="Review Words"
      intro="Tap every unknown word. The rest will be marked as known."
      :summary="reviewSummary"
      more-label="Review More Words"
      confirm-label="Finish"
      @more="reviewMore"
      @confirm="confirm"
    />

    <section v-else-if="phase === 'result' && result" class="flex flex-col gap-5">
      <div class="grid grid-cols-1 gap-4" :class="{ 'md:grid-cols-2': result.retentionEncounters }">
        <div class="rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-5 flex flex-col gap-2">
          <span class="text-[11px] font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">Marked as known</span>
          <span class="text-3xl font-bold tabular-nums">{{ result.seeded.toLocaleString() }} words</span>
          <p class="text-sm text-gray-700 dark:text-gray-300">
            This is an estimate. You can reset any single word from its page, or undo the whole check anytime in Settings.
          </p>
        </div>
        <div
          v-if="result.retentionEncounters"
          class="rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-5 flex flex-col gap-2"
        >
          <span class="text-[11px] font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">In your reading</span>
          <span class="text-xl font-bold">You know most words you've met about {{ result.retentionEncounters }} times or more</span>
          <p class="text-sm text-gray-700 dark:text-gray-300">Worked out from your answers and how often each word appears in what you read.</p>
        </div>
      </div>

      <section
        v-if="result.studyList.length > 0"
        class="rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 overflow-hidden"
      >
        <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-3 p-4">
          <div class="flex flex-col gap-0.5">
            <h3 class="font-bold">Words you met often that aren't marked yet</h3>
            <span class="text-[13px] text-gray-600 dark:text-gray-300">
              {{ result.studyList.length.toLocaleString() }} words. Those seen across several titles come first, since they are the most likely to come up
              again.
            </span>
          </div>
          <Button
            :label="studyDeckCreated ? 'Study deck added' : 'Study these words'"
            :icon="studyDeckCreated ? 'pi pi-check' : undefined"
            :disabled="studyDeckCreated"
            :loading="busy"
            class="shrink-0"
            @click="studyThese"
          />
        </div>
        <div class="overflow-x-auto">
          <table class="w-full text-sm">
            <thead>
              <tr class="text-left text-xs text-gray-600 dark:text-gray-300 border-y border-gray-200 dark:border-neutral-700">
                <th class="px-4 py-2 font-semibold">Word</th>
                <th class="px-4 py-2 font-semibold">Reading</th>
                <th class="px-4 py-2 font-semibold">Meaning</th>
                <th class="px-4 py-2 font-semibold text-right whitespace-nowrap">Times seen</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="word in showAllStudy ? result.studyList : result.studyList.slice(0, 10)"
                :key="`${word.wordId}-${word.readingIndex}`"
                class="border-b border-gray-100 dark:border-neutral-800 last:border-0"
              >
                <td class="px-4 py-2">
                  <NuxtLink :to="`/vocabulary/${word.wordId}/${word.readingIndex}`" lang="ja" class="text-base whitespace-nowrap" target="_blank">{{
                    word.text
                  }}</NuxtLink>
                </td>
                <td lang="ja" class="px-4 py-2 whitespace-nowrap">{{ word.reading }}</td>
                <td class="px-4 py-2 text-gray-700 dark:text-gray-300">{{ word.meaning }}</td>
                <td class="px-4 py-2 text-right tabular-nums">{{ word.occurrences }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <div v-if="result.studyList.length > 10" class="px-4 py-3 border-t border-gray-200 dark:border-neutral-700">
          <Button
            :label="showAllStudy ? 'Show fewer' : `Show all ${result.studyList.length.toLocaleString()}`"
            link
            class="!p-0 !text-sm"
            @click="showAllStudy = !showAllStudy"
          />
        </div>
      </section>
    </section>
  </div>
</template>
