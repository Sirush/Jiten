<script setup lang="ts">
  import { toggled } from '~/utils/toggled';

  withDefaults(
    defineProps<{
      items: { id: number; text: string }[];
      intro: string;
      busy: boolean;
      title?: string;
      summary?: string;
      confirmLabel?: string;
      moreLabel?: string;
      columns?: 4 | 5;
    }>(),
    { title: 'One last check', summary: undefined, confirmLabel: 'Confirm', moreLabel: undefined, columns: 5 }
  );
  const emit = defineEmits<{ confirm: []; more: [] }>();
  const unknownIds = defineModel<Set<number>>({ required: true });
</script>

<template>
  <section class="flex flex-col gap-5">
    <div class="flex flex-col gap-1">
      <h2 class="text-2xl font-bold">{{ title }}</h2>
      <p class="text-gray-600 dark:text-gray-300">{{ intro }}</p>
    </div>
    <div class="grid grid-cols-2 gap-3" :class="columns === 4 ? 'sm:grid-cols-4' : 'sm:grid-cols-5'">
      <button
        v-for="item in items"
        :key="item.id"
        type="button"
        :aria-pressed="unknownIds.has(item.id)"
        class="min-h-20 rounded-xl border flex flex-col items-center justify-center gap-0.5 transition-colors focus-visible:outline-2 focus-visible:outline-primary-500"
        :class="
          unknownIds.has(item.id)
            ? 'border-orange-600 ring-1 ring-orange-600 bg-orange-50 dark:bg-orange-950/40 dark:border-orange-500 dark:ring-orange-500'
            : 'border-gray-200 bg-white dark:bg-neutral-900 dark:border-neutral-700 hover:border-primary-400'
        "
        @click="unknownIds = toggled(unknownIds, item.id)"
      >
        <span lang="ja" class="px-2 text-center leading-tight" :class="item.text.length > 4 ? 'text-lg' : 'text-2xl'">{{ item.text }}</span>
        <span class="text-xs font-semibold text-orange-800 dark:text-orange-300" :class="unknownIds.has(item.id) ? '' : 'invisible'">Don't know</span>
      </button>
    </div>
    <div class="flex flex-wrap items-center justify-end gap-3">
      <span v-if="summary" class="mr-auto text-sm text-gray-600 dark:text-gray-300 tabular-nums" aria-live="polite">{{ summary }}</span>
      <Button v-if="moreLabel" :label="moreLabel" severity="secondary" outlined :disabled="busy" @click="emit('more')" />
      <Button :label="confirmLabel" :loading="busy" @click="emit('confirm')" />
    </div>
  </section>
</template>
