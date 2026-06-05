<script setup lang="ts">
  import { FsrsRating, type RecentReviewDto } from '~/types';
  import { stripRuby } from '~/utils/stripRuby';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Review History' });

  const route = useRoute();

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));

  const {
    data: response,
    status,
  } = await useApiFetchPaginated<RecentReviewDto[]>('srs/review-history', {
    query: { offset },
    watch: [offset],
  });

  const { start, end, totalItems, previousLink, nextLink } = usePagination(response);

  function ratingLabel(rating: FsrsRating) {
    return { [FsrsRating.Again]: 'Again', [FsrsRating.Hard]: 'Hard', [FsrsRating.Good]: 'Good', [FsrsRating.Easy]: 'Easy' }[rating] ?? '';
  }

  function ratingColor(rating: FsrsRating) {
    return {
      [FsrsRating.Again]: 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400',
      [FsrsRating.Hard]: 'bg-orange-100 text-orange-700 dark:bg-orange-900/30 dark:text-orange-400',
      [FsrsRating.Good]: 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400',
      [FsrsRating.Easy]: 'bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400',
    }[rating] ?? '';
  }

  function formatDateTime(dateStr: string) {
    const d = new Date(dateStr);
    return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
      + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  }

  function formatDuration(ms: number) {
    return (ms / 1000).toFixed(1) + 's';
  }

  function wordDisplay(review: RecentReviewDto) {
    return review.wordText ? stripRuby(review.wordText) : `${review.wordId}`;
  }
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <SrsSubNav />
    <div class="flex flex-wrap items-center justify-between gap-2 mb-4 min-h-[2.5rem]">
      <h1 class="text-2xl font-bold">Review History</h1>
    </div>

    <PaginationControls :previous-link="previousLink" :next-link="nextLink" :start="start" :end="end" :total-items="totalItems" item-label="reviews" />

    <div v-if="status === 'pending'" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm overflow-hidden divide-y divide-surface-100 dark:divide-surface-800">
      <div
        v-for="i in 10"
        :key="i"
        class="flex items-center gap-2 py-2 px-3"
      >
        <div class="h-4 w-28 rounded bg-surface-200 dark:bg-surface-700 animate-pulse shrink-0" />
        <div class="h-4 w-24 rounded bg-surface-200 dark:bg-surface-700 animate-pulse" />
        <div class="h-4 w-[40px] rounded bg-surface-200 dark:bg-surface-700 animate-pulse ml-auto shrink-0" />
        <div class="h-5 w-[48px] rounded bg-surface-200 dark:bg-surface-700 animate-pulse shrink-0" />
      </div>
    </div>

    <template v-else-if="response?.data?.length">
      <div class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm overflow-hidden divide-y divide-surface-100 dark:divide-surface-800">
        <div
          v-for="(review, i) in response.data"
          :key="i"
          class="flex items-center gap-2 text-sm py-2 px-3 hover:bg-surface-50 dark:hover:bg-surface-800/50 transition-colors"
        >
          <span class="text-surface-500 text-sm shrink-0">{{ formatDateTime(review.reviewDateTime) }}</span>
          <NuxtLink
            :to="`/vocabulary/${review.wordId}/${review.readingIndex}/reviews`"
            class="font-medium text-sm text-primary hover:underline truncate"
          >
            {{ wordDisplay(review) }}
          </NuxtLink>
          <span class="text-surface-400 text-sm shrink-0 w-[40px] text-right ml-auto">
            {{ review.reviewDuration != null ? formatDuration(review.reviewDuration) : '' }}
          </span>
          <span class="px-1.5 py-0.5 rounded text-sm font-medium w-[48px] text-center shrink-0" :class="ratingColor(review.rating)">
            {{ ratingLabel(review.rating) }}
          </span>
        </div>
      </div>
    </template>

    <div v-else class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-12 text-center text-surface-400">
      No reviews yet
    </div>

    <PaginationControls
      :previous-link="previousLink"
      :next-link="nextLink"
      :start="start"
      :end="end"
      :total-items="totalItems"
      :show-summary="false"
      :scroll-to-top-on-next="true"
    />
  </div>
</template>
