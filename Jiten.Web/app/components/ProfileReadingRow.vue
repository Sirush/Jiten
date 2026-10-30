<script setup lang="ts">
  import type { ProfileDeck } from '~/types';
  import { coverUrl } from '~/utils/coverImage';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { entryProgressLabel, entryProgressPercent } from '~/utils/mediaListEntry';

  const props = defineProps<{ item: ProfileDeck }>();

  const localiseTitle = useLocaliseTitle();

  const progress = computed(() => {
    const entry = props.item.listEntry;
    const label = entryProgressLabel(entry, props.item.deck.characterCount);
    if (!entry || !label) return null;
    const percent = entry.unitCount
      ? Math.round(((entry.completedUnits ?? 0) / entry.unitCount) * 100)
      : (entryProgressPercent(entry, props.item.deck.characterCount) ?? 0);
    return { label, percent };
  });
</script>

<template>
  <div class="flex min-w-0 gap-3">
    <NuxtLink
      :to="`/decks/media/${item.deck.deckId}/detail`"
      class="block h-[88px] w-[60px] shrink-0 overflow-hidden rounded bg-surface-200 dark:bg-surface-800"
    >
      <img :src="coverUrl(item.deck.coverName, 'sm')" :alt="localiseTitle(item.deck)" loading="lazy" class="h-full w-full object-cover" />
    </NuxtLink>
    <div class="flex min-w-0 flex-1 flex-col gap-1">
      <NuxtLink
        :to="`/decks/media/${item.deck.deckId}/detail`"
        class="truncate font-semibold !text-surface-900 !no-underline hover:!text-primary-600 dark:!text-surface-0 dark:hover:!text-primary-300"
        :title="localiseTitle(item.deck)"
        v-bind="japaneseTextAttrs(localiseTitle(item.deck))"
      >
        {{ localiseTitle(item.deck) }}
      </NuxtLink>
      <span class="text-xs text-muted-color">{{ getMediaTypeText(item.deck.mediaType) }}</span>
      <div v-if="progress" class="flex items-center gap-2 text-xs">
        <div class="h-1.5 flex-1 overflow-hidden rounded bg-surface-200 dark:bg-surface-700" aria-hidden="true">
          <div class="h-full rounded bg-green-600 dark:bg-green-500" :style="{ width: `${progress.percent}%` }" />
        </div>
        <span class="whitespace-nowrap tabular-nums">{{ progress.label }}</span>
      </div>
    </div>
  </div>
</template>
