<script setup lang="ts">
  import type { StudyMoreParams, StudyMoreMode } from '~/types';

  const props = defineProps<{
    visible: boolean;
    reviewBudgetHit: boolean;
  }>();

  const emit = defineEmits<{
    'update:visible': [value: boolean];
    select: [params: StudyMoreParams];
  }>();

  const { $api } = useNuxtApp();

  const localVisible = ref(props.visible);
  watch(() => props.visible, (v) => { localVisible.value = v; });
  watch(localVisible, (v) => { emit('update:visible', v); });

  const selectedMode = ref<StudyMoreMode | null>(null);

  const extraNewAmount = ref(10);
  const extraNewCustom = ref(20);
  const extraNewOptions = [10, 20, 0] as const;

  const extraReviewAmount = ref(50);
  const extraReviewCustom = ref(100);
  const extraReviewOptions = [50, 100, 0] as const;

  const aheadOption = ref(1440);
  const aheadCustomHours = ref(6);
  const aheadOptions = [
    { label: '1 hour', value: 60 },
    { label: '1 day', value: 1440 },
    { label: '3 days', value: 4320 },
    { label: 'Custom', value: 0 },
  ];

  const mistakeOption = ref(3);
  const mistakeOptions = [
    { label: '1 day', value: 1 },
    { label: '3 days', value: 3 },
    { label: '7 days', value: 7 },
  ];

  watch(() => props.visible, (v) => {
    if (v) selectedMode.value = null;
  });

  const modes = computed(() => [
    {
      id: 'extraNew' as StudyMoreMode,
      icon: 'material-symbols:add-circle-outline',
      title: 'Learn More New Cards',
      description: 'Temporarily increase today\'s new card limit',
      always: true,
    },
    {
      id: 'extraReview' as StudyMoreMode,
      icon: 'material-symbols:replay',
      title: 'Increase Review Limit',
      description: 'Add more reviews beyond your daily cap',
      always: false,
      hidden: !props.reviewBudgetHit,
    },
    {
      id: 'ahead' as StudyMoreMode,
      icon: 'material-symbols:fast-forward-outline',
      title: 'Review Ahead',
      description: 'Study cards due in the next few days early',
      always: true,
    },
    {
      id: 'mistakes' as StudyMoreMode,
      icon: 'material-symbols:error-outline',
      title: 'Study Recent Mistakes',
      description: 'Re-study cards you got wrong recently',
      always: true,
    },
  ]);

  const visibleModes = computed(() => modes.value.filter(m => !m.hidden));

  function selectMode(mode: StudyMoreMode) {
    selectedMode.value = selectedMode.value === mode ? null : mode;
  }

  function getNewCardValue(): number {
    return extraNewAmount.value === 0 ? Math.max(1, extraNewCustom.value) : extraNewAmount.value;
  }

  function getExtraReviewValue(): number {
    return extraReviewAmount.value === 0 ? Math.max(1, extraReviewCustom.value) : extraReviewAmount.value;
  }

  function getAheadMinutes(): number {
    return aheadOption.value === 0 ? Math.max(60, aheadCustomHours.value * 60) : aheadOption.value;
  }

  // Card count preview
  const previewCount = ref<number | null>(null);
  const isLoadingCount = ref(false);
  let countAbortController: AbortController | null = null;
  let countDebounceTimer: ReturnType<typeof setTimeout> | null = null;

  function fetchCount() {
    if (countDebounceTimer) clearTimeout(countDebounceTimer);
    countDebounceTimer = setTimeout(doFetchCount, 300);
  }

  async function doFetchCount() {
    const mode = selectedMode.value;
    if (!mode) { previewCount.value = null; return; }

    countAbortController?.abort();
    countAbortController = new AbortController();

    const params = new URLSearchParams({ mode });
    switch (mode) {
      case 'extraNew':
        params.set('extraNewCards', String(getNewCardValue()));
        break;
      case 'extraReview':
        params.set('extraReviews', String(getExtraReviewValue()));
        break;
      case 'ahead':
        params.set('aheadMinutes', String(getAheadMinutes()));
        break;
      case 'mistakes':
        params.set('mistakeDays', String(mistakeOption.value));
        break;
    }

    isLoadingCount.value = true;
    try {
      const result = await $api<{ count: number }>(`srs/study-more-count?${params}`, {
        signal: countAbortController.signal,
      });
      previewCount.value = result.count;
    } catch (e: any) {
      if (e?.name !== 'AbortError') previewCount.value = null;
    } finally {
      isLoadingCount.value = false;
    }
  }

  watch(selectedMode, () => { previewCount.value = null; fetchCount(); });
  watch(extraNewAmount, fetchCount);
  watch(extraNewCustom, fetchCount);
  watch(extraReviewAmount, fetchCount);
  watch(extraReviewCustom, fetchCount);
  watch(aheadOption, fetchCount);
  watch(aheadCustomHours, fetchCount);
  watch(mistakeOption, fetchCount);

  function handleStart() {
    if (!selectedMode.value) return;

    const params: StudyMoreParams = { mode: selectedMode.value };
    switch (selectedMode.value) {
      case 'extraNew':
        params.extraNewCards = getNewCardValue();
        break;
      case 'extraReview':
        params.extraReviews = getExtraReviewValue();
        break;
      case 'ahead':
        params.aheadMinutes = getAheadMinutes();
        break;
      case 'mistakes':
        params.mistakeDays = mistakeOption.value;
        break;
    }
    emit('select', params);
  }
</script>

<template>
  <Dialog v-model:visible="localVisible" header="Study More" :modal="true" class="w-full md:w-[28rem]">
    <div class="flex flex-col gap-3">
      <div
        v-for="mode in visibleModes"
        :key="mode.id"
        class="rounded-xl border transition-all cursor-pointer"
        :class="selectedMode === mode.id
          ? 'border-primary-500 bg-primary-50 dark:bg-primary-950/20 ring-1 ring-primary-500'
          : 'border-surface-200 dark:border-surface-700 hover:border-surface-300 dark:hover:border-surface-600'"
        @click="selectMode(mode.id)"
      >
        <div class="flex items-center gap-3 px-4 py-3">
          <Icon :name="mode.icon" size="1.5rem" class="shrink-0" :class="selectedMode === mode.id ? 'text-primary-500' : 'text-surface-400'" />
          <div class="min-w-0">
            <div class="font-medium text-sm" :class="selectedMode === mode.id ? 'text-primary-700 dark:text-primary-300' : ''">{{ mode.title }}</div>
            <div class="text-xs text-surface-500 dark:text-surface-400">{{ mode.description }}</div>
          </div>
        </div>

        <!-- Expanded options -->
        <div v-if="selectedMode === mode.id" class="px-4 pb-3 pt-1" @click.stop>
          <!-- Extra New Cards -->
          <template v-if="mode.id === 'extraNew'">
            <div class="flex gap-2 flex-wrap">
              <button
                v-for="opt in extraNewOptions"
                :key="opt"
                class="px-3 py-1.5 rounded-lg text-sm font-medium transition-colors"
                :class="extraNewAmount === opt
                  ? 'bg-primary-500 text-white'
                  : 'bg-surface-100 dark:bg-surface-800 text-surface-600 dark:text-surface-300 hover:bg-surface-200 dark:hover:bg-surface-700'"
                @click="extraNewAmount = opt"
              >
                {{ opt === 0 ? 'Custom' : `+${opt}` }}
              </button>
            </div>
            <div v-if="extraNewAmount === 0" class="mt-3 flex items-center gap-3">
              <Slider v-model="extraNewCustom" :min="1" :max="200" class="flex-1" />
              <InputNumber v-model="extraNewCustom" :min="1" fluid class="max-w-22 flex-shrink-0" size="small" :input-style="{ textAlign: 'center' }" />
            </div>
          </template>

          <!-- Extra Reviews -->
          <template v-if="mode.id === 'extraReview'">
            <div class="flex gap-2 flex-wrap">
              <button
                v-for="opt in extraReviewOptions"
                :key="opt"
                class="px-3 py-1.5 rounded-lg text-sm font-medium transition-colors"
                :class="extraReviewAmount === opt
                  ? 'bg-primary-500 text-white'
                  : 'bg-surface-100 dark:bg-surface-800 text-surface-600 dark:text-surface-300 hover:bg-surface-200 dark:hover:bg-surface-700'"
                @click="extraReviewAmount = opt"
              >
                {{ opt === 0 ? 'Custom' : `+${opt}` }}
              </button>
            </div>
            <div v-if="extraReviewAmount === 0" class="mt-3 flex items-center gap-3">
              <Slider v-model="extraReviewCustom" :min="1" :max="500" class="flex-1" />
              <InputNumber v-model="extraReviewCustom" :min="1" :max="500" fluid class="max-w-22 flex-shrink-0" size="small" :input-style="{ textAlign: 'center' }" />
            </div>
          </template>

          <!-- Review Ahead -->
          <template v-if="mode.id === 'ahead'">
            <div class="flex gap-2 flex-wrap">
              <button
                v-for="opt in aheadOptions"
                :key="opt.value"
                class="px-3 py-1.5 rounded-lg text-sm font-medium transition-colors"
                :class="aheadOption === opt.value
                  ? 'bg-primary-500 text-white'
                  : 'bg-surface-100 dark:bg-surface-800 text-surface-600 dark:text-surface-300 hover:bg-surface-200 dark:hover:bg-surface-700'"
                @click="aheadOption = opt.value"
              >
                {{ opt.label }}
              </button>
            </div>
            <div v-if="aheadOption === 0" class="mt-3 flex items-center gap-3">
              <Slider v-model="aheadCustomHours" :min="1" :max="168" class="flex-1" />
              <InputNumber v-model="aheadCustomHours" :min="1" :max="168" suffix=" hrs" fluid class="max-w-22 flex-shrink-0" size="small" :input-style="{ textAlign: 'center' }" />
            </div>
          </template>

          <!-- Recent Mistakes -->
          <template v-if="mode.id === 'mistakes'">
            <div class="flex gap-2 flex-wrap">
              <button
                v-for="opt in mistakeOptions"
                :key="opt.value"
                class="px-3 py-1.5 rounded-lg text-sm font-medium transition-colors"
                :class="mistakeOption === opt.value
                  ? 'bg-primary-500 text-white'
                  : 'bg-surface-100 dark:bg-surface-800 text-surface-600 dark:text-surface-300 hover:bg-surface-200 dark:hover:bg-surface-700'"
                @click="mistakeOption = opt.value"
              >
                Last {{ opt.label }}
              </button>
            </div>
          </template>

          <!-- Card count preview -->
          <div class="mt-2 text-xs tabular-nums h-4" :class="previewCount === 0 ? 'text-amber-500' : 'text-surface-400'">
            <span v-if="isLoadingCount" class="inline-block w-3 h-3 border-2 border-surface-300 dark:border-surface-600 border-t-primary-500 rounded-full animate-spin align-middle mr-1" />
            <span v-else-if="previewCount !== null">{{ previewCount }} {{ previewCount === 1 ? 'card' : 'cards' }} available</span>
            <span v-else>&nbsp;</span>
          </div>
        </div>
      </div>
    </div>

    <div class="mt-4">
      <Button
        label="Start"
        class="w-full"
        :disabled="!selectedMode || previewCount === 0"
        @click="handleStart"
      />
    </div>
  </Dialog>
</template>
