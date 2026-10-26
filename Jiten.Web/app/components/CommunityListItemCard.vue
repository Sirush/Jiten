<script setup lang="ts">
  import { DeckStatus } from '~/types';
  import type { CommunityListItem } from '~/types/types';
  import { communityListItemAnchor } from '~/composables/useCommunityLists';
  import { coverUrl } from '~/utils/coverImage';
  import { getDeckStatusColourClass, getDeckStatusText } from '~/utils/deckStatusMapper';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';

  const props = withDefaults(defineProps<{ item: CommunityListItem; compact?: boolean; showViewerData?: boolean; planning?: boolean }>(), {
    compact: false,
    showViewerData: false,
    planning: false,
  });

  const emit = defineEmits<{ plan: [] }>();

  const localiseTitle = useLocaliseTitle();

  const deck = computed(() => props.item.deck);
  const anchorId = computed(() => communityListItemAnchor(props.item.position));
  const rankLinkClass =
    'text-right font-bold tabular-nums text-muted-color! no-underline! hover:text-purple-600! focus-visible:outline-2 focus-visible:outline-purple-500 dark:hover:text-purple-300!';
  const deckPath = computed(() => `/decks/media/${deck.value.deckId}/detail`);
  const title = computed(() => localiseTitle(deck.value));
  const subtitle = computed(() => {
    const original = deck.value.originalTitle;
    return title.value === original ? (deck.value.englishTitle ?? deck.value.romajiTitle ?? null) : original;
  });
  const hasStatus = computed(() => (deck.value.status ?? DeckStatus.None) !== DeckStatus.None);
  const coverageLabel = computed(() => Math.round(deck.value.coverage));
  const matureWidth = computed(() => `${Math.min(deck.value.coverage, 100).toFixed(1)}%`);
  const youngWidth = computed(() => `${Math.max(0, Math.min(deck.value.youngCoverage, 100 - deck.value.coverage)).toFixed(1)}%`);
</script>

<template>
  <li
    v-if="!compact"
    :id="anchorId"
    class="flex scroll-mt-4 flex-col rounded-lg border border-surface-200 bg-surface-0 target:ring-2 target:ring-purple-400 dark:border-surface-700 dark:bg-surface-900"
  >
    <div class="flex flex-col gap-3 p-3 sm:p-4" :class="{ 'pb-2 sm:pb-0.5': item.note }">
      <div class="flex items-start gap-3 sm:gap-4">
        <a :href="`#${anchorId}`" :class="rankLinkClass" class="w-6 shrink-0 pt-0.5 text-lg sm:w-8 sm:text-2xl" :aria-label="`Link to #${item.position}`">{{
          item.position
        }}</a>
        <NuxtLink :to="deckPath" tabindex="-1" aria-hidden="true" class="shrink-0">
          <img :src="coverUrl(deck.coverName, 'sm')" alt="" loading="lazy" class="h-24 w-16 rounded object-cover sm:h-30 sm:w-20" />
        </NuxtLink>
        <div class="flex min-w-0 flex-1 flex-col gap-1">
          <NuxtLink
            :to="deckPath"
            class="text-base font-bold text-surface-900! no-underline! hover:text-primary-700! sm:text-lg dark:text-surface-0! dark:hover:text-primary-300!"
            v-bind="japaneseTextAttrs(title)"
          >
            {{ title }}
          </NuxtLink>
          <span v-if="subtitle" class="truncate text-sm text-muted-color" v-bind="japaneseTextAttrs(subtitle)">{{ subtitle }}</span>
          <div class="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-color">
            <span class="font-semibold text-surface-800 dark:text-surface-100">{{ getMediaTypeText(deck.mediaType) }}</span>
            <DifficultyDisplay :difficulty="deck.difficulty" :difficulty-raw="deck.difficultyRaw" class="text-sm" />
            <span v-if="deck.characterCount > 0">{{ deck.characterCount.toLocaleString() }} characters</span>
          </div>
        </div>
        <div v-if="showViewerData" class="hidden w-36 shrink-0 flex-col gap-2 pt-0.5 sm:flex">
          <DeckCoverageBars :deck="deck" dense />
          <span v-if="hasStatus" class="flex items-center gap-1.5 text-sm font-semibold" :class="getDeckStatusColourClass(deck.status)">
            <i class="pi pi-flag-fill text-xs" aria-hidden="true" />{{ getDeckStatusText(deck.status) }}
          </span>
          <Button
            v-else
            label="Planning"
            icon="pi pi-plus"
            size="small"
            outlined
            class="self-start"
            :loading="planning"
            :aria-label="`Add ${title} to Planning`"
            @click="emit('plan')"
          />
        </div>
      </div>
      <div v-if="showViewerData" class="flex items-center gap-3 sm:hidden">
        <span class="shrink-0 text-xs text-muted-color">Your coverage</span>
        <div class="flex h-1.5 flex-1 overflow-hidden rounded-full bg-surface-200 dark:bg-surface-700" aria-hidden="true">
          <div class="bg-purple-500" :style="{ width: matureWidth }" />
          <div class="bg-purple-300 dark:bg-purple-800" :style="{ width: youngWidth }" />
        </div>
        <span class="w-9 shrink-0 text-right text-xs font-bold tabular-nums">{{ coverageLabel }}%</span>
        <span v-if="hasStatus" class="shrink-0 text-xs font-semibold" :class="getDeckStatusColourClass(deck.status)">{{ getDeckStatusText(deck.status) }}</span>
        <Button
          v-else
          icon="pi pi-plus"
          size="small"
          outlined
          class="shrink-0"
          :loading="planning"
          :aria-label="`Add ${title} to Planning`"
          @click="emit('plan')"
        />
      </div>
    </div>
    <p
      v-if="item.note"
      class="m-0 rounded-b-lg border-t border-purple-100 bg-purple-50 px-3 py-2 text-sm leading-relaxed text-surface-700 sm:pr-4 sm:pl-16 dark:border-purple-900/60 dark:bg-purple-950/40 dark:text-surface-200"
    >
      <span class="block max-w-[68ch]"
        ><i class="pi pi-comment mr-1.5 text-xs text-purple-600 dark:text-purple-300" aria-hidden="true" />{{ item.note }}</span
      >
    </p>
  </li>

  <li v-else :id="anchorId" class="flex scroll-mt-4 items-center gap-3 px-3 py-2 target:bg-purple-50 sm:px-4 dark:target:bg-purple-950/40">
    <a :href="`#${anchorId}`" :class="rankLinkClass" class="w-6 shrink-0 text-sm" :aria-label="`Link to #${item.position}`">{{ item.position }}</a>
    <img :src="coverUrl(deck.coverName, 'xs')" alt="" loading="lazy" class="h-[42px] w-[30px] shrink-0 rounded-sm object-cover" />
    <div class="flex min-w-0 flex-1 flex-col">
      <div class="flex min-w-0 items-baseline gap-2">
        <NuxtLink
          :to="deckPath"
          class="truncate font-semibold text-surface-900! no-underline! hover:text-primary-700! dark:text-surface-0! dark:hover:text-primary-300!"
          v-bind="japaneseTextAttrs(title)"
        >
          {{ title }}
        </NuxtLink>
        <span class="shrink-0 text-xs text-muted-color">{{ getMediaTypeText(deck.mediaType) }}</span>
      </div>
      <span class="truncate text-sm text-muted-color">{{ item.note ?? subtitle }}</span>
    </div>
    <template v-if="showViewerData">
      <div class="hidden w-28 shrink-0 items-center gap-2 sm:flex">
        <div class="flex h-1.5 flex-1 overflow-hidden rounded-full bg-surface-200 dark:bg-surface-700" aria-hidden="true">
          <div class="bg-purple-500" :style="{ width: matureWidth }" />
          <div class="bg-purple-300 dark:bg-purple-800" :style="{ width: youngWidth }" />
        </div>
        <span class="w-9 text-right text-sm font-bold tabular-nums" :aria-label="`Your coverage ${coverageLabel}%`">{{ coverageLabel }}%</span>
      </div>
      <div class="flex shrink-0 justify-end sm:w-28">
        <span v-if="hasStatus" class="text-sm font-semibold" :class="getDeckStatusColourClass(deck.status)">{{ getDeckStatusText(deck.status) }}</span>
        <Button
          v-else
          icon="pi pi-plus"
          label="Planning"
          size="small"
          outlined
          class="[&_.p-button-label]:hidden sm:[&_.p-button-label]:inline"
          :loading="planning"
          :aria-label="`Add ${title} to Planning`"
          @click="emit('plan')"
        />
      </div>
    </template>
  </li>
</template>
