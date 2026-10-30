<script setup lang="ts">
  import { formatScore } from '~/utils/formatScore';

  const score = defineModel<number | null>({ required: true });

  const props = defineProps<{
    labelledBy?: string;
    disabled?: boolean;
  }>();

  const STEPS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
  const WORDS = ['Appalling', 'Awful', 'Bad', 'Weak', 'Average', 'Fine', 'Good', 'Very good', 'Excellent', 'Masterpiece'];

  const slider = ref<HTMLElement>();
  const exactInput = ref<HTMLInputElement>();
  const preview = ref<number | null>(null);
  const typing = ref(false);
  const typed = ref('');

  const shown = computed(() => preview.value ?? score.value);
  const wordFor = (value: number | null) => (value == null ? 'No score' : WORDS[Math.floor(value) - 1]!);
  const valueText = computed(() => (score.value == null ? 'No score' : `${formatScore(score.value)}, ${wordFor(score.value)}`));

  const clamp = (value: number) => Math.min(10, Math.max(1, Math.round(value * 10) / 10));
  const fillOf = (step: number) => (shown.value == null ? 0 : Math.min(1, Math.max(0, shown.value - (step - 1))));

  /** The left half of a segment picks the half point below it. */
  function valueAt(e: MouseEvent, step: number) {
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    return clamp(e.clientX - rect.left < rect.width / 2 ? step - 0.5 : step);
  }

  function set(value: number | null) {
    if (props.disabled) return;
    score.value = value == null ? null : clamp(value);
  }

  function onKeydown(e: KeyboardEvent) {
    const fine = e.shiftKey ? 0.1 : 0.5;
    const current = score.value;
    let next: number | null | undefined;
    switch (e.key) {
      case 'ArrowRight':
      case 'ArrowUp':
        next = current == null ? 1 : current + fine;
        break;
      case 'ArrowLeft':
      case 'ArrowDown':
        next = current == null ? undefined : current - fine;
        break;
      case 'Home':
        next = 1;
        break;
      case 'End':
        next = 10;
        break;
      case 'Backspace':
      case 'Delete':
        next = null;
        break;
      default:
        if (/^[0-9]$/.test(e.key)) next = e.key === '0' ? 10 : Number(e.key);
    }
    if (next === undefined) return;
    e.preventDefault();
    set(next);
  }

  async function startTyping() {
    if (props.disabled) return;
    typed.value = score.value == null ? '' : formatScore(score.value);
    typing.value = true;
    await nextTick();
    exactInput.value?.select();
  }

  /** Empty clears the score; anything unparsable keeps the previous one. */
  function commitTyped() {
    if (!typing.value) return;
    typing.value = false;
    const raw = typed.value.trim().replace(',', '.');
    if (!raw) set(null);
    else if (Number.isFinite(Number(raw))) set(Number(raw));
  }

  defineExpose({ focus: () => slider.value?.focus() });
</script>

<template>
  <div class="flex flex-col gap-2" :class="{ 'pointer-events-none opacity-60': disabled }">
    <div class="flex h-9 items-baseline gap-1.5">
      <input
        v-if="typing"
        ref="exactInput"
        v-model="typed"
        type="text"
        inputmode="decimal"
        aria-label="Exact enjoyment score, 1 to 10"
        class="-ml-1 w-[4.5rem] rounded border border-surface-300 bg-transparent px-1 text-3xl font-semibold leading-tight tabular-nums text-primary-600 outline-none focus:border-primary-500 dark:border-surface-600 dark:text-primary-300"
        @keydown.enter="commitTyped"
        @keydown.escape.prevent.stop="typing = false"
        @blur="commitTyped"
      />
      <span v-else-if="shown == null" class="self-center text-sm text-muted-color">Not scored yet</span>
      <button
        v-else
        type="button"
        class="-ml-1 cursor-text rounded px-1 text-3xl font-semibold leading-tight tabular-nums transition-colors hover:bg-surface-100 dark:hover:bg-surface-800"
        :class="preview != null ? 'text-primary-400 dark:text-primary-500' : 'text-primary-600 dark:text-primary-300'"
        aria-label="Type an exact enjoyment score"
        :disabled="disabled"
        @click="startTyping"
      >
        {{ formatScore(shown) }}
      </button>
      <template v-if="typing || shown != null">
        <span class="text-sm text-muted-color">/ 10</span>
        <span class="ml-1.5 text-sm font-medium text-surface-600 dark:text-surface-300">{{ wordFor(shown) }}</span>
      </template>
      <button
        v-if="score != null"
        type="button"
        class="ml-auto self-center rounded px-1.5 py-0.5 text-xs text-muted-color transition-colors hover:bg-surface-100 hover:text-surface-700 dark:hover:bg-surface-800 dark:hover:text-surface-200"
        :disabled="disabled"
        @click="set(null)"
      >
        Clear
      </button>
    </div>
    <div
      ref="slider"
      role="slider"
      :tabindex="disabled ? -1 : 0"
      :aria-labelledby="labelledBy"
      aria-valuemin="1"
      aria-valuemax="10"
      :aria-valuenow="score ?? undefined"
      :aria-valuetext="valueText"
      :aria-disabled="disabled || undefined"
      class="-mx-1 flex rounded-md outline-none focus-visible:ring-2 focus-visible:ring-primary-500"
      @keydown="onKeydown"
      @pointerleave="preview = null"
    >
      <div
        v-for="step in STEPS"
        :key="step"
        class="group flex flex-1 cursor-pointer flex-col items-center gap-1.5 px-0.5 pb-0.5 pt-1.5"
        @pointermove="preview = valueAt($event, step)"
        @click="set(valueAt($event, step))"
      >
        <div class="h-1.5 w-full overflow-hidden rounded-full bg-surface-200 transition-transform group-hover:scale-y-150 dark:bg-surface-700">
          <div
            class="h-full rounded-full transition-[width] duration-100"
            :class="preview != null ? 'bg-primary-300 dark:bg-primary-600' : 'bg-primary-500'"
            :style="{ width: `${fillOf(step) * 100}%` }"
          />
        </div>
        <span
          class="text-[11px] tabular-nums transition-colors"
          :class="shown != null && Math.ceil(shown) === step ? 'font-semibold text-primary-600 dark:text-primary-300' : 'text-muted-color'"
          >{{ step }}</span
        >
      </div>
    </div>
  </div>
</template>
