<script setup lang="ts">
  import { FsrsRating, FsrsState, type ReviewHistoryDto } from '~/types';
  import { stripRuby } from '~/utils/stripRuby';
  import type { Word } from '~/types/types';

  definePageMeta({ middleware: ['auth'] });

  const route = useRoute();
  const wordId = Number(route.params.wordId) || 0;
  const readingIndex = Number(route.params.readingIndex) || 0;

  const { data: wordData } = await useApiFetch<Word>(`vocabulary/${wordId}/${readingIndex}/info`);
  const { data, pending } = await useApiFetch<ReviewHistoryDto>(`srs/review-history/${wordId}/${readingIndex}`);

  const title = computed(() => {
    if (wordData.value?.mainReading?.text) return stripRuby(wordData.value.mainReading.text);
    return 'Word';
  });

  useHead(() => ({
    title: `Review History - ${title.value}`,
  }));

  const stateLabel = computed(() => {
    if (!data.value?.card) return '';
    const map: Record<number, string> = {
      [FsrsState.Learning]: 'Learning',
      [FsrsState.Review]: 'Review',
      [FsrsState.Relearning]: 'Relearning',
      [FsrsState.Blacklisted]: 'Blacklisted',
      [FsrsState.Mastered]: 'Mastered',
      [FsrsState.Suspended]: 'Suspended',
    };
    return map[data.value.card.state] ?? 'Unknown';
  });

  const stateColor = computed(() => {
    if (!data.value?.card) return '';
    const map: Record<number, string> = {
      [FsrsState.Learning]: 'text-blue-500',
      [FsrsState.Review]: 'text-green-500',
      [FsrsState.Relearning]: 'text-orange-500',
      [FsrsState.Blacklisted]: 'text-gray-500',
      [FsrsState.Mastered]: 'text-green-600',
      [FsrsState.Suspended]: 'text-gray-400',
    };
    return map[data.value.card.state] ?? '';
  });

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

  function formatDate(dateStr: string) {
    return new Date(dateStr).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
  }

  function formatDateTime(dateStr: string) {
    const d = new Date(dateStr);
    return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
      + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  }

  function formatDuration(ms: number) {
    return (ms / 1000).toFixed(1) + 's';
  }

  function formatStability(days: number) {
    if (days < 1) return `${Math.round(days * 24)}h`;
    if (days < 30) return `${Math.round(days)}d`;
    return `${(days / 30).toFixed(1)}mo`;
  }
</script>

<template>
  <div class="max-w-2xl mx-auto px-4 py-6">
    <div class="mb-4">
      <NuxtLink :to="`/vocabulary/${wordId}/${readingIndex}`" class="text-sm text-primary hover:underline">
        <i class="pi pi-arrow-left text-xs mr-1" />
        {{ title }}
      </NuxtLink>
    </div>

    <h1 class="text-2xl font-bold mb-6">Review History</h1>

    <div v-if="pending" class="flex justify-center py-12">
      <i class="pi pi-spin pi-spinner text-2xl text-surface-400" />
    </div>

    <template v-else-if="data">
      <template v-if="data.card">
        <div class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-5 mb-6">
          <div class="grid grid-cols-2 sm:grid-cols-3 gap-x-6 gap-y-3 text-sm">
            <div>
              <div class="text-surface-400 text-xs uppercase tracking-wide mb-0.5">State</div>
              <div class="font-medium" :class="stateColor">{{ stateLabel }}</div>
            </div>
            <div>
              <div class="text-surface-400 text-xs uppercase tracking-wide mb-0.5">Stability</div>
              <div class="font-medium">{{ data.card.stability != null ? formatStability(data.card.stability) : '—' }}</div>
            </div>
            <div>
              <div class="text-surface-400 text-xs uppercase tracking-wide mb-0.5">Difficulty</div>
              <div class="font-medium">{{ data.card.difficulty != null ? data.card.difficulty.toFixed(1) + '/10' : '—' }}</div>
            </div>
            <div>
              <div class="text-surface-400 text-xs uppercase tracking-wide mb-0.5">Lapses</div>
              <div class="font-medium" :class="data.card.lapses >= 8 ? 'text-red-600 dark:text-red-400' : ''">{{ data.card.lapses }}</div>
            </div>
            <div>
              <div class="text-surface-400 text-xs uppercase tracking-wide mb-0.5">Due</div>
              <div class="font-medium">{{ formatDate(data.card.due) }}</div>
            </div>
            <div>
              <div class="text-surface-400 text-xs uppercase tracking-wide mb-0.5">Created</div>
              <div class="font-medium">{{ formatDate(data.card.createdAt) }}</div>
            </div>
            <div v-if="data.card.lastReview">
              <div class="text-surface-400 text-xs uppercase tracking-wide mb-0.5">Last review</div>
              <div class="font-medium">{{ formatDate(data.card.lastReview) }}</div>
            </div>
          </div>
        </div>

        <div class="text-sm text-surface-400 mb-3">{{ data.reviews.length }} review{{ data.reviews.length !== 1 ? 's' : '' }}</div>

        <div v-if="data.reviews.length > 0" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm overflow-hidden divide-y divide-surface-100 dark:divide-surface-800">
          <div
            v-for="(review, i) in data.reviews"
            :key="i"
            class="flex items-center gap-3 text-sm py-3 px-4 hover:bg-surface-50 dark:hover:bg-surface-800/50 transition-colors"
          >
            <span class="text-surface-500 min-w-[150px]">{{ formatDateTime(review.reviewDateTime) }}</span>
            <span class="px-2 py-0.5 rounded text-xs font-medium min-w-[48px] text-center" :class="ratingColor(review.rating)">
              {{ ratingLabel(review.rating) }}
            </span>
            <span v-if="review.reviewDuration != null" class="text-surface-400 text-xs">
              {{ formatDuration(review.reviewDuration) }}
            </span>
          </div>
        </div>
      </template>

      <div v-else class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-12 text-center text-surface-400">
        No reviews yet
      </div>
    </template>
  </div>
</template>
