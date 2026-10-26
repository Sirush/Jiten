<script setup lang="ts">
  import { CommunityListStatus } from '~/types';
  import type { CommunityListSummary } from '~/types/types';
  import { COMMUNITY_LIST_STATUS_TEXT } from '~/composables/useCommunityLists';
  import { coverUrl } from '~/utils/coverImage';
  import { getGenreText } from '~/utils/genreMapper';
  import { formatDateLong } from '~/utils/dateFormat';
  import { formatRelativeTime } from '~/utils/relativeTime';

  const props = withDefaults(defineProps<{ list: CommunityListSummary; compact?: boolean }>(), { compact: false });

  /** Percent of the 2:1 strip one 2:3 cover spans at full height. */
  const COVER_WIDTH = 100 / 3;

  const covers = computed(() => props.list.previewCoverNames);

  /** The first cover sits on top; each later one tucks under the one before it, showing an equal slice on its right. */
  function coverStyle(index: number) {
    const step = Math.min(COVER_WIDTH, (100 - COVER_WIDTH) / Math.max(1, covers.value.length - 1));
    return { left: `${index * step}%`, width: `${COVER_WIDTH}%`, zIndex: covers.value.length - index };
  }

  const statusLabel = computed(() =>
    props.list.status == null || props.list.status === CommunityListStatus.Public ? null : COMMUNITY_LIST_STATUS_TEXT[props.list.status]
  );
</script>

<template>
  <NuxtLink
    :to="communityListPath(list)"
    class="group flex flex-col overflow-hidden rounded-lg border border-surface-200 bg-surface-0 text-inherit no-underline! transition-colors hover:border-primary-300 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 dark:border-surface-700 dark:bg-surface-900 dark:hover:border-primary-700"
    :class="compact ? 'w-64 shrink-0 lg:w-[calc((100%-0.75rem)/2)]' : 'w-full'"
  >
    <div class="relative aspect-[2/1] overflow-hidden bg-surface-100 dark:bg-surface-800" aria-hidden="true">
      <img
        v-for="(cover, index) in covers"
        :key="index"
        :src="coverUrl(cover, 'md')"
        alt=""
        loading="lazy"
        class="absolute inset-y-0 h-full object-cover"
        :class="{ 'shadow-[3px_0_6px_rgba(0,0,0,0.35)]': index < covers.length - 1 }"
        :style="coverStyle(index)"
      />
    </div>
    <div class="flex flex-1 flex-col gap-1.5 p-3">
      <div class="flex items-start gap-2">
        <span
          class="line-clamp-2 flex-1 font-semibold leading-snug text-surface-900 group-hover:text-primary-700 dark:text-surface-0 dark:group-hover:text-primary-300"
        >
          {{ list.name }}
        </span>
        <Tag v-if="statusLabel" :value="statusLabel" severity="secondary" class="shrink-0 text-xs!" />
      </div>
      <span v-if="list.ownerDisplayName" class="truncate text-sm text-muted-color">by {{ list.ownerDisplayName }}</span>
      <div v-if="list.genres.length" class="flex flex-wrap gap-1">
        <span
          v-for="genre in list.genres"
          :key="genre"
          class="rounded bg-purple-100 px-1.5 py-px text-xs text-purple-800 dark:bg-purple-900/50 dark:text-purple-200"
        >
          {{ getGenreText(genre) }}
        </span>
      </div>
      <div class="mt-auto flex items-center gap-3 pt-1 text-sm whitespace-nowrap text-muted-color">
        <span class="flex items-center gap-1" :aria-label="`${list.itemCount} ${list.itemCount === 1 ? 'title' : 'titles'}`">
          <i class="pi pi-book text-xs" aria-hidden="true" />
          {{ list.itemCount.toLocaleString() }}
        </span>
        <span v-if="list.favouriteCount > 0" class="flex items-center gap-1" :aria-label="`${list.favouriteCount} favourites`">
          <i class="pi text-xs" :class="list.isFavourited ? 'pi-heart-fill text-pink-600 dark:text-pink-400' : 'pi-heart'" aria-hidden="true" />
          {{ list.favouriteCount.toLocaleString() }}
        </span>
        <ClientOnly>
          <Tooltip :content="`Updated ${formatDateLong(list.updatedAt)}`">
            <span class="ml-auto flex min-w-0 items-center gap-1" :aria-label="`Updated ${formatRelativeTime(list.updatedAt, undefined, 'short')}`">
              <i class="pi pi-clock text-xs" aria-hidden="true" />
              <span class="truncate">{{ formatRelativeTime(list.updatedAt, undefined, 'short') }}</span>
            </span>
          </Tooltip>
          <template #fallback>
            <Skeleton width="5rem" height="1.25rem" class="ml-auto" aria-hidden="true" />
          </template>
        </ClientOnly>
      </div>
    </div>
  </NuxtLink>
</template>
