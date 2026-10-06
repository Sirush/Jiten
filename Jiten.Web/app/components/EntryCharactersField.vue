<script setup lang="ts">
  import { MAX_CHARACTERS_READ, rawPercentOf } from '~/utils/mediaListEntry';

  const props = defineProps<{
    modelValue: number | null;
    deckCharacters: number;
    completed: boolean;
    fallback?: number | null;
    fallbackFrom?: string;
    label?: string;
    onCommit?: (value: number | null) => unknown;
  }>();

  const draft = ref<number | null>(props.modelValue);
  const typing = ref(false);
  let commitTimer: ReturnType<typeof setTimeout> | undefined;
  let savesInFlight = 0;
  let lastSent: number | null = null;

  watch(
    () => props.modelValue,
    (value) => {
      if (!typing.value && commitTimer === undefined && savesInFlight === 0) draft.value = value;
    }
  );

  const hasFallback = computed(() => props.fallback != null && props.fallback > 0);
  const effective = computed(() => draft.value ?? (hasFallback.value ? props.fallback! : props.completed ? props.deckCharacters : 0));
  const hasSlider = computed(() => props.deckCharacters > 0);
  const percent = computed(() => (hasSlider.value ? rawPercentOf(effective.value, props.deckCharacters) : 0));

  // A completed entry with no count stands for the whole deck, so its slider cannot reach 0%.
  const minimum = computed(() => (props.completed ? 1 : 0));

  const sliderPercent = computed({
    get: () => Math.min(percent.value, 100),
    set: (value: number) => {
      draft.value = Math.ceil((value * props.deckCharacters) / 100);
      scheduleCommit();
    },
  });

  function scheduleCommit() {
    clearTimeout(commitTimer);
    commitTimer = setTimeout(commit, 500);
  }

  onBeforeUnmount(() => {
    if (commitTimer !== undefined || typing.value) void commit();
  });

  function normalise(value: number | null): number | null {
    if (!value || value <= 0) return null;
    value = Math.min(value, MAX_CHARACTERS_READ);
    if (hasFallback.value ? value === props.fallback : props.completed && value === props.deckCharacters) return null;
    return value;
  }

  async function commit() {
    clearTimeout(commitTimer);
    commitTimer = undefined;
    const value = normalise(draft.value);
    draft.value = value;
    if (value === (savesInFlight > 0 ? lastSent : props.modelValue)) return;
    lastSent = value;
    savesInFlight++;
    try {
      await props.onCommit?.(value);
    } finally {
      savesInFlight--;
    }
  }

  function onTyped(value: number | string | null | undefined) {
    draft.value = typeof value === 'number' ? Math.min(value, MAX_CHARACTERS_READ) : null;
  }

  function onBlur() {
    typing.value = false;
    void commit();
  }

  function reset() {
    draft.value = null;
    void commit();
  }

  const inputValue = computed(() => (draft.value == null && (hasFallback.value || !props.completed) ? null : effective.value));
  const placeholder = computed(() => (hasFallback.value ? props.fallback!.toLocaleString() : 'Not counted'));

  const fallbackNote = computed(() => {
    if (!hasFallback.value) return null;
    const fallback = props.fallback!.toLocaleString();
    if (draft.value == null) return `Added up from its ${props.fallbackFrom}`;
    if (props.completed || draft.value > props.fallback!) return `Replaces the ${fallback} its ${props.fallbackFrom} add up to`;
    return `The ${props.fallbackFrom} adds up to ${fallback} which is higher and you might want to use instead.`;
  });
  const resetLabel = computed(() => {
    if (hasFallback.value) return `Use the count from its ${props.fallbackFrom}`;
    return props.completed ? 'Use the full count' : 'Clear';
  });
</script>

<template>
  <div class="flex flex-col gap-2 text-xs text-gray-600 dark:text-gray-300">
    <div class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5">
      <span>{{ label ?? 'Characters read' }}</span>
      <span v-if="hasSlider" class="tabular-nums">
        <span class="font-semibold" :class="percent > 100 ? 'text-primary-600 dark:text-primary-300' : 'text-gray-800 dark:text-gray-100'">{{ percent }}%</span>
        of {{ deckCharacters.toLocaleString() }} characters
      </span>
    </div>

    <div class="flex items-center gap-3">
      <Slider
        v-if="hasSlider"
        v-model="sliderPercent"
        :min="minimum"
        :max="100"
        :step="1"
        class="flex-1 min-w-0"
        :aria-label="`${label ?? 'Characters read'}, as a percentage of ${deckCharacters.toLocaleString()} characters`"
      />
      <InputNumber
        :model-value="inputValue"
        :min="minimum"
        :max="MAX_CHARACTERS_READ"
        :placeholder="placeholder"
        size="small"
        class="w-32 shrink-0"
        input-class="w-full tabular-nums"
        :aria-label="label ?? 'Characters read'"
        @input="onTyped($event.value)"
        @focus="typing = true"
        @blur="onBlur"
        @keydown.enter="commit"
      />
    </div>

    <div v-if="modelValue != null || fallbackNote" class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
      <span>{{ fallbackNote }}</span>
      <button v-if="modelValue != null" type="button" class="text-primary-600 dark:text-primary-300 hover:underline cursor-pointer" @click="reset">
        {{ resetLabel }}
      </button>
    </div>
  </div>
</template>
