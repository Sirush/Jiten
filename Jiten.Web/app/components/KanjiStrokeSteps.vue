<script setup lang="ts">
  import { storeToRefs } from 'pinia';
  import type { KanjiStrokes } from '~/types';
  import { useJitenStore } from '~/stores/jitenStore';

  const props = defineProps<{
    strokes: KanjiStrokes;
    character: string;
    showCredit: boolean;
  }>();

  const FRAME_SIZES = ['w-16 h-16', 'w-28 h-28', 'w-40 h-40'];

  const { kanjiStrokeStepsOpen: open, kanjiStrokeStepsZoom: storedZoom } = storeToRefs(useJitenStore());
  const zoom = computed(() => Math.min(Math.max(storedZoom.value, 0), FRAME_SIZES.length - 1));

  const startPoint = (d: string) => {
    const match = /^M\s*(-?[\d.]+)[,\s]+(-?[\d.]+)/.exec(d);
    return match ? { x: Number(match[1]), y: Number(match[2]) } : null;
  };

  const steps = computed(() => props.strokes.paths.map((d, i) => ({ index: i, start: startPoint(d) })));
</script>

<template>
  <div class="border-surface-200 dark:border-surface-700 border rounded-lg">
    <button
      type="button"
      class="w-full flex items-center justify-between gap-2 p-4 text-left cursor-pointer"
      :aria-expanded="open"
      aria-controls="kanji-stroke-steps"
      @click="open = !open"
    >
      <h2 class="text-lg font-semibold">
        Stroke order
        <span class="ml-1 text-sm font-normal text-surface-500 dark:text-surface-400">({{ strokes.paths.length }} strokes)</span>
      </h2>
      <i class="pi text-surface-500 dark:text-surface-400" :class="open ? 'pi-chevron-up' : 'pi-chevron-down'" aria-hidden="true" />
    </button>

    <div v-if="open" id="kanji-stroke-steps" class="px-4 pb-4">
      <div class="flex justify-end gap-1 mb-2">
        <Button
          icon="pi pi-search-minus"
          size="small"
          severity="secondary"
          text
          rounded
          aria-label="Smaller frames"
          :disabled="zoom === 0"
          @click="storedZoom = zoom - 1"
        />
        <Button
          icon="pi pi-search-plus"
          size="small"
          severity="secondary"
          text
          rounded
          aria-label="Larger frames"
          :disabled="zoom === FRAME_SIZES.length - 1"
          @click="storedZoom = zoom + 1"
        />
      </div>
      <ol class="flex flex-wrap gap-1.5" :aria-label="`Stroke order for ${character}, one stroke per step`">
        <li v-for="step in steps" :key="step.index">
          <svg
            viewBox="0 0 109 109"
            class="rounded-md border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900"
            :class="FRAME_SIZES[zoom]"
            role="img"
            :aria-label="`Stroke ${step.index + 1}`"
          >
            <g class="text-surface-200 dark:text-surface-700" stroke="currentColor" stroke-width="0.75" stroke-dasharray="3 3">
              <line x1="54.5" y1="0" x2="54.5" y2="109" />
              <line x1="0" y1="54.5" x2="109" y2="54.5" />
            </g>
            <g fill="none" stroke="currentColor" :stroke-width="zoom === 0 ? 5 : 4" stroke-linecap="round" stroke-linejoin="round">
              <path v-for="i in step.index" :key="i" :d="strokes.paths[i - 1]" class="text-surface-400 dark:text-surface-500" />
              <path :d="strokes.paths[step.index]" class="text-surface-900 dark:text-surface-0" />
            </g>
            <circle v-if="step.start" :cx="step.start.x" :cy="step.start.y" :r="zoom === 0 ? 5 : 4" fill="currentColor" class="text-primary-500 dark:text-primary-400" />
            <text v-if="zoom > 0" x="4" y="12" font-size="10" fill="currentColor" class="text-surface-500 dark:text-surface-400">{{ step.index + 1 }}</text>
          </svg>
        </li>
      </ol>
      <p class="mt-2 text-[11px] text-surface-500 dark:text-surface-400">The dot marks where each stroke starts.</p>
      <KanjiVgCredit v-if="showCredit" />
    </div>
  </div>
</template>
