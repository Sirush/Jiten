<script setup lang="ts">
  import type { OnboardingPath, WordCheckLevel, WordCheckResult } from '~/types/wordCheck';

  definePageMeta({ middleware: ['auth'], bare: true, fullBleed: true });
  useHead({ title: 'Set up your known words' });

  const route = useRoute();
  const router = useRouter();
  const toast = useToast();
  const { state, refresh, update } = useOnboarding();

  type StepKey = 'level' | 'check' | 'import' | 'read' | 'title';
  const allSteps: { key: StepKey; label: string }[] = [
    { key: 'level', label: 'Your level' },
    { key: 'check', label: 'Word check' },
    { key: 'import', label: 'Import' },
    { key: 'read', label: 'Reading check' },
    { key: 'title', label: 'First title' },
  ];

  const level = ref<WordCheckLevel | null>(state.value?.level ?? null);
  const ready = ref(!!state.value);
  const checkResult = ref<WordCheckResult | null>(null);
  const checkSettled = ref(false);
  const retaking = ref(route.query.retake === '1');
  const asideRefreshKey = ref(0);

  const previousCheck = computed(() =>
    !retaking.value && state.value?.wordCheckDoneAt
      ? { words: state.value.wordCheckWords ?? 0, at: state.value.wordCheckDoneAt, batchId: state.value.wordCheckBatchId }
      : null
  );
  const checkWords = computed(() => checkResult.value?.wordsKnown ?? previousCheck.value?.words ?? 0);

  const steps = computed(() =>
    level.value === 'Starting' && route.query.step !== 'read' ? allSteps.filter((s) => s.key === 'level' || s.key === 'title') : allSteps
  );

  /** The reading check needs no level, so Settings can open it directly. */
  const step = computed<StepKey>(() => {
    const raw = route.query.step as StepKey | undefined;
    if (!raw || !steps.value.some((s) => s.key === raw)) return 'level';
    if (raw !== 'level' && raw !== 'read' && !level.value) return 'level';
    return raw;
  });

  function go(key: StepKey) {
    router.push({ query: { ...route.query, step: key } });
    if (import.meta.client) window.scrollTo({ top: 0 });
  }

  function move(delta: 1 | -1) {
    const i = steps.value.findIndex((s) => s.key === step.value);
    const target = steps.value[i + delta];
    if (target) go(target.key);
  }

  const savingLevel = ref(false);
  async function onLevelContinue() {
    if (!level.value) return;
    savingLevel.value = true;
    try {
      await update({ level: level.value });
      move(1);
    } catch (error) {
      toast.add({ severity: 'error', detail: extractApiError(error, 'Could not save your level. Try again.'), life: 5000 });
    } finally {
      savingLevel.value = false;
    }
  }

  function onCheckStarted() {
    checkResult.value = null;
    retaking.value = true;
    if (route.query.retake) {
      const { retake: _, ...query } = route.query;
      router.replace({ query });
    }
  }

  function onCheckDone(result: WordCheckResult) {
    checkResult.value = result;
    retaking.value = false;
    onKnowledgeChanged();
  }

  function onKnowledgeChanged() {
    asideRefreshKey.value++;
    refresh().catch(() => {});
  }

  function onPath(path: OnboardingPath) {
    update({ path }).catch(() => {});
  }

  async function setUpLater() {
    await update({ dismissed: true }).catch(() => {});
    await router.push('/');
  }

  onMounted(async () => {
    const current = await refresh().catch(() => state.value);
    if (current?.level && !level.value) level.value = current.level;
    ready.value = true;
    if (current && !current.openedAt) update({ opened: true }).catch(() => {});
  });
</script>

<template>
  <div class="flex-1 bg-gray-50 dark:bg-neutral-950 flex flex-col">
    <header class="h-16 shrink-0 flex items-center justify-between px-4 md:px-8 bg-white dark:bg-neutral-900 border-b border-gray-200 dark:border-neutral-800">
      <NuxtLink to="/" class="text-[22px] font-bold !text-primary-700 dark:!text-primary-300 !no-underline">Jiten</NuxtLink>
      <Button label="Set up later" link class="!px-0 !text-sm" @click="setUpLater" />
    </header>

    <main class="w-full max-w-5xl mx-auto px-4 pt-8 pb-16 flex flex-col gap-8">
      <template v-if="!ready">
        <Skeleton height="1.5rem" class="md:mx-16 !w-auto" />
        <Skeleton height="20rem" border-radius="0.75rem" class="max-w-3xl w-full mx-auto" />
      </template>

      <template v-else>
        <WelcomeStepper :steps="steps" :current="step" class="md:mx-16" />

        <WelcomeLevelStep v-if="step === 'level'" v-model="level" :saving="savingLevel" class="max-w-3xl w-full mx-auto" @continue="onLevelContinue" />

        <div v-else-if="step === 'check'" class="flex flex-col gap-6">
          <h1 class="sr-only">Word check</h1>
          <WordCheckRunner
            :level="level"
            :previous="previousCheck"
            @started="onCheckStarted"
            @done="onCheckDone"
            @undone="onKnowledgeChanged"
            @changed="onKnowledgeChanged"
            @settled="checkSettled = $event"
          />
          <div v-if="checkSettled" class="flex flex-col gap-4 max-w-xl w-full mx-auto">
            <section
              v-if="(checkWords > 0 || level === 'Advanced') && !state?.readingCheck"
              class="rounded-xl border-2 border-primary-600 dark:border-primary-400 bg-white dark:bg-neutral-900 p-5 flex flex-col gap-2"
            >
              <span class="text-[11px] font-semibold uppercase tracking-wide text-primary-700 dark:text-primary-300">Coming up in step 4</span>
              <h2 class="text-xl font-bold">Add the words from what you've read</h2>
              <p class="text-sm text-gray-700 dark:text-gray-300">
                The word check you've just finished marked the most common words you know. In step 4, you'll be able to pick the titles you've finished to add
                the words you've learned from them.
              </p>
            </section>
            <div class="flex justify-end">
              <Button label="Continue" @click="move(1)" />
            </div>
          </div>
        </div>

        <div v-else class="flex flex-col lg:flex-row gap-6 items-start">
          <WelcomeImportStep v-if="step === 'import'" class="flex-1 min-w-0 w-full" @changed="onKnowledgeChanged" @back="move(-1)" @continue="move(1)" />
          <WelcomeReadStep v-else-if="step === 'read'" class="flex-1 min-w-0 w-full" @changed="onKnowledgeChanged" @back="move(-1)" @continue="move(1)" />
          <WelcomeFirstTitleStep v-else class="flex-1 min-w-0 w-full" :level="level" @back="move(-1)" @path="onPath" />
          <WelcomeKnownAside v-if="step !== 'title'" :since="state?.openedAt ?? null" :refresh-key="asideRefreshKey" class="w-full lg:w-72 shrink-0" />
        </div>
      </template>
    </main>
  </div>
</template>
