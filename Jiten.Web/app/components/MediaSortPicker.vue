<script setup lang="ts">
  import { type DeckSortGroup, deckSortLabels } from '~/utils/deckSorting';

  const props = defineProps<{
    groups: DeckSortGroup[];
    modelValue: string;
  }>();

  const emit = defineEmits<{ 'update:modelValue': [value: string] }>();

  const pick = (key: string) => emit('update:modelValue', key);

  const isActive = (key: string) => props.modelValue === key;

  const focusRing = 'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500';
  const activeFill = 'bg-primary-600 font-medium text-white';
  const idleFill = 'text-surface-700 hover:bg-surface-100 dark:text-surface-200 dark:hover:bg-surface-800';

  const chipClass = (key: string) => [
    'rounded-md border px-2.5 py-1 text-sm transition-colors',
    focusRing,
    isActive(key) ? `border-primary-600 ${activeFill}` : `border-surface-300 dark:border-surface-700 ${idleFill}`,
  ];

  const segmentClass = (key: string) => [
    'border-l border-surface-300 px-2.5 py-1 text-sm transition-colors dark:border-surface-700',
    'focus-visible:outline-2 focus-visible:-outline-offset-4 focus-visible:outline-current',
    isActive(key) ? activeFill : idleFill,
  ];

  const groupId = (label: string) => `sort-group-${label.replace(/\W+/g, '-').toLowerCase()}`;
</script>

<template>
  <div class="flex flex-col gap-3">
    <div v-for="group in groups" :key="group.label" role="group" :aria-labelledby="groupId(group.label)" class="flex flex-col gap-1.5">
      <div :id="groupId(group.label)" class="text-xs font-semibold text-surface-500 dark:text-surface-400">{{ group.label }}</div>
      <div class="flex flex-wrap gap-1.5">
        <template v-for="entry in group.entries" :key="entry.label">
          <button
            v-if="entry.choices.length === 1"
            type="button"
            :aria-pressed="isActive(entry.choices[0]!.key)"
            :class="chipClass(entry.choices[0]!.key)"
            @click="pick(entry.choices[0]!.key)"
          >
            {{ deckSortLabels[entry.choices[0]!.key] ?? entry.choices[0]!.label }}
          </button>
          <div
            v-else
            role="group"
            :aria-label="entry.label"
            class="inline-flex items-stretch overflow-hidden rounded-md border"
            :class="entry.choices.some((c) => isActive(c.key)) ? 'border-primary-600' : 'border-surface-300 dark:border-surface-700'"
          >
            <span class="flex items-center px-2.5 py-1 text-sm text-surface-600 dark:text-surface-300">{{ entry.label }}</span>
            <button
              v-for="choice in entry.choices"
              :key="choice.key"
              type="button"
              :aria-pressed="isActive(choice.key)"
              :aria-label="deckSortLabels[choice.key] ?? `${entry.label} ${choice.label}`"
              :class="segmentClass(choice.key)"
              @click="pick(choice.key)"
            >
              {{ choice.label }}
            </button>
          </div>
        </template>
      </div>
    </div>
  </div>
</template>
