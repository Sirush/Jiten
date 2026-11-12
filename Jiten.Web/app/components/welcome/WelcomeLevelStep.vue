<script setup lang="ts">
  import { WORD_CHECK_LEVEL_TITLES, type WordCheckLevel } from '~/types/wordCheck';

  defineProps<{ saving?: boolean }>();
  const model = defineModel<WordCheckLevel | null>({ required: true });
  const emit = defineEmits<{ continue: [] }>();

  const levels: { value: WordCheckLevel; description: string; outcome: string }[] = [
    {
      value: 'Starting',
      description: 'You know kana and maybe a few words.',
      outcome: "You'll start with no tracked words. Good luck with your studies!",
    },
    {
      value: 'Beginner',
      description: 'You know the basics, about what Kaishi 1.5k or Genki I and II cover.',
      outcome: "You'll be offered a quick 5-minute word check to mark the words you already know.",
    },
    {
      value: 'Intermediate',
      description: 'You can follow simple manga or anime while looking up some words.',
      outcome: "You'll be offered a quick 5-minute word check with harder words.",
    },
    {
      value: 'Advanced',
      description: "You're comfortable reading novels with a few lookups or watching anime without subtitles.",
      outcome: "You'll be offered a quick 5-minute word check with the hardest words.",
    },
  ];

  function onKeydown(event: KeyboardEvent, index: number) {
    const delta = event.key === 'ArrowDown' || event.key === 'ArrowRight' ? 1 : event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 0;
    if (!delta) return;
    event.preventDefault();
    const next = levels[(index + delta + levels.length) % levels.length]!;
    model.value = next.value;
    nextTick(() => document.getElementById(`level-${next.value}`)?.focus());
  }
</script>

<template>
  <div class="flex flex-col gap-6">
    <div class="flex flex-col gap-1.5">
      <h1 class="text-2xl md:text-3xl font-bold">What's your level?</h1>
      <p class="text-gray-600 dark:text-gray-300">Pick the description that fits you the closest. It will be used to help choose the best words to test you on.</p>
    </div>

    <div role="radiogroup" aria-label="Your level" class="flex flex-col gap-2.5">
      <button
        v-for="(level, i) in levels"
        :id="`level-${level.value}`"
        :key="level.value"
        type="button"
        role="radio"
        :aria-checked="model === level.value"
        :tabindex="model === level.value || (!model && i === 0) ? 0 : -1"
        class="w-full text-left flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4 px-4 py-3.5 sm:px-5 rounded-xl border transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500"
        :class="
          model === level.value
            ? 'border-primary-600 ring-1 ring-primary-600 bg-primary-50 dark:bg-primary-950/40 dark:border-primary-400 dark:ring-primary-400'
            : 'border-gray-200 bg-white dark:bg-neutral-900 dark:border-neutral-700 hover:border-primary-400'
        "
        @click="model = level.value"
        @keydown="onKeydown($event, i)"
      >
        <span class="flex items-center gap-4 flex-1 min-w-0">
          <span
            aria-hidden="true"
            class="w-5 h-5 rounded-full shrink-0 bg-white dark:bg-neutral-900"
            :class="model === level.value ? 'border-[6px] border-primary-600 dark:border-primary-400' : 'border-2 border-gray-300 dark:border-neutral-600'"
          />
          <span class="flex flex-col gap-0.5 min-w-0">
            <span class="font-bold">{{ WORD_CHECK_LEVEL_TITLES[level.value] }}</span>
            <span class="text-sm text-gray-600 dark:text-gray-300">{{ level.description }}</span>
          </span>
        </span>
        <span class="text-[13px] text-gray-600 dark:text-gray-300 sm:w-64 sm:shrink-0 pl-9 sm:pl-0">{{ level.outcome }}</span>
      </button>
    </div>

    <p class="flex items-start gap-2.5 px-3.5 py-3 rounded-lg bg-gray-100 dark:bg-neutral-800 text-sm text-gray-700 dark:text-gray-300">
      <Icon name="material-symbols:info-outline-rounded" size="1.25em" class="shrink-0 text-primary-700 dark:text-primary-300 mt-px" />
      Coming from Anki or JPDB? You'll be able to import your knowledge from there in step 3.
    </p>

    <div class="flex justify-end">
      <Button label="Continue" :disabled="!model" :loading="saving" @click="emit('continue')" />
    </div>
  </div>
</template>
