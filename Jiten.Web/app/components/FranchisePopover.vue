<script setup lang="ts">
  import type { RelationCaption } from '~/composables/useFranchiseGraph';
  import type { FranchisePopoverMembership } from '~/utils/franchiseLayout';

  defineProps<{
    deckId: number;
    title: string;
    captions: RelationCaption[];
    memberships: FranchisePopoverMembership[];
  }>();

  const emit = defineEmits<{
    goto: [deckId: number];
    rowHover: [deckId: number];
    rowLeave: [];
    membership: [membership: FranchisePopoverMembership];
  }>();
</script>

<template>
  <div
    class="fixed z-50 flex max-h-80 flex-col overflow-y-auto rounded-md border border-surface-200 bg-surface-0 shadow-lg dark:border-surface-700 dark:bg-surface-900"
    role="dialog"
    :aria-label="title"
  >
    <div class="flex items-baseline justify-between gap-2 border-b border-surface-200 px-2 py-1.5 dark:border-surface-700">
      <span class="truncate text-xs font-semibold" :title="title" v-bind="japaneseTextAttrs(title)">{{ title }}</span>
      <NuxtLink :to="`/decks/media/${deckId}/detail`" class="shrink-0 text-xs font-semibold text-primary hover:underline">Open →</NuxtLink>
    </div>
    <div class="flex flex-col gap-0.5 p-2">
      <button
        v-for="(c, i) in captions"
        :key="`c-${i}`"
        type="button"
        class="flex w-full min-w-0 items-baseline gap-1.5 rounded px-1 py-0.5 text-left text-xs hover:bg-surface-100 dark:hover:bg-surface-800"
        @mouseenter="emit('rowHover', c.otherId)"
        @mouseleave="emit('rowLeave')"
        @click="emit('goto', c.otherId)"
      >
        <span class="shrink-0 font-semibold text-gray-500 dark:text-gray-400">{{ c.label }}:</span>
        <span class="truncate text-surface-700 dark:text-surface-200" v-bind="japaneseTextAttrs(c.otherTitle)">{{ c.otherTitle }}</span>
      </button>
      <p v-if="!captions.length" class="px-1 py-0.5 text-xs text-gray-500 dark:text-gray-400">No direct relations.</p>
      <template v-if="memberships.length">
        <div class="my-1 border-t border-surface-200 dark:border-surface-700" />
        <template v-for="m in memberships" :key="m.key">
          <NuxtLink
            v-if="m.to"
            :to="m.to"
            class="flex w-full min-w-0 items-baseline gap-1.5 rounded px-1 py-0.5 text-xs hover:bg-surface-100 dark:hover:bg-surface-800"
          >
            <span class="shrink-0 font-semibold text-gray-500 dark:text-gray-400">{{ m.label }}:</span>
            <span class="truncate text-surface-700 dark:text-surface-200" v-bind="japaneseTextAttrs(m.name)">{{ m.name }}</span>
          </NuxtLink>
          <p v-else-if="m.inert" class="m-0 flex w-full min-w-0 items-baseline gap-1.5 px-1 py-0.5 text-xs">
            <span class="shrink-0 font-semibold text-gray-500 dark:text-gray-400">{{ m.label }}:</span>
            <span class="truncate text-surface-700 dark:text-surface-200" v-bind="japaneseTextAttrs(m.name)">{{ m.name }}</span>
          </p>
          <button
            v-else
            type="button"
            class="flex w-full min-w-0 items-baseline gap-1.5 rounded px-1 py-0.5 text-left text-xs hover:bg-surface-100 dark:hover:bg-surface-800"
            @click="emit('membership', m)"
          >
            <span class="shrink-0 font-semibold text-gray-500 dark:text-gray-400">{{ m.label }}:</span>
            <span class="truncate text-surface-700 dark:text-surface-200" v-bind="japaneseTextAttrs(m.name)">{{ m.name }}</span>
          </button>
        </template>
      </template>
    </div>
  </div>
</template>
