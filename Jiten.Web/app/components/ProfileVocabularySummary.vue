<script setup lang="ts">
  import type { ProfileVocabularyStats } from '~/types';

  const props = defineProps<{
    username: string;
  }>();

  const emit = defineEmits<{
    loaded: [stats: ProfileVocabularyStats | null];
  }>();

  const { $api } = useNuxtApp();

  const isLoading = ref(true);
  const stats = ref<ProfileVocabularyStats | null>(null);

  const fetchStats = async () => {
    isLoading.value = true;
    try {
      stats.value = await $api<ProfileVocabularyStats>(`user/profile/${props.username}/vocabulary-stats`);
    } catch {
      stats.value = null;
    } finally {
      isLoading.value = false;
      emit('loaded', stats.value);
    }
  };

  watch(() => props.username, () => fetchStats());
  onMounted(() => fetchStats());

  const knownWords = computed(() => {
    if (!stats.value) return 0;
    return stats.value.young + stats.value.mature + stats.value.mastered;
  });

  const hasData = computed(() => knownWords.value > 0 || (stats.value?.wordSetMastered ?? 0) > 0);

  const segments = computed(() => {
    const s = stats.value;
    if (!s || knownWords.value === 0) return [];
    return [
      { key: 'young', label: 'Young', count: s.young, dot: 'bg-yellow-500 dark:bg-yellow-300', bar: 'bg-yellow-500 dark:bg-yellow-300' },
      { key: 'mature', label: 'Mature', count: s.mature, dot: 'bg-green-500 dark:bg-green-400', bar: 'bg-green-500 dark:bg-green-400' },
      { key: 'mastered', label: 'Mastered', count: s.mastered, dot: 'bg-teal-600 dark:bg-teal-400', bar: 'bg-teal-600 dark:bg-teal-400' },
    ].filter((segment) => segment.count > 0);
  });

  const formatNumber = (num: number) => num.toLocaleString();

  const percent = (count: number) => (count / knownWords.value) * 100;
</script>

<template>
  <Card v-if="isLoading || hasData">
    <template #title>
      <div class="flex items-center gap-2">
        <Icon name="material-symbols:book-2-outline" />
        Vocabulary
      </div>
    </template>
    <template #content>
      <div v-if="isLoading" class="flex flex-col gap-3">
        <Skeleton width="12rem" height="2.25rem" />
        <Skeleton width="100%" height="0.75rem" />
        <Skeleton width="18rem" height="1rem" />
      </div>

      <div v-else class="flex flex-col gap-3">
        <div>
          <span class="text-[clamp(1.5rem,6vw,2.25rem)] font-bold tabular-nums text-primary-600 dark:text-primary-300">
            {{ formatNumber(knownWords) }}
          </span>
          <span class="text-gray-500 dark:text-gray-400 ml-2">{{ knownWords === 1 ? 'word known' : 'words known' }}</span>
        </div>

        <div v-if="segments.length" class="flex h-3 w-full overflow-hidden rounded-full bg-surface-200 dark:bg-surface-700">
          <div
            v-for="segment in segments"
            :key="segment.key"
            :class="segment.bar"
            :style="{ width: `${percent(segment.count)}%`, minWidth: '3px' }"
            class="h-full" />
        </div>

        <div v-if="segments.length" class="flex flex-wrap gap-x-6 gap-y-2">
          <div v-for="segment in segments" :key="segment.key" class="flex items-center gap-2">
            <span :class="segment.dot" class="inline-block h-2.5 w-2.5 shrink-0 rounded-full" />
            <span class="text-sm text-gray-600 dark:text-gray-300">{{ segment.label }}</span>
            <span class="text-sm font-semibold tabular-nums">{{ formatNumber(segment.count) }}</span>
          </div>
        </div>

        <p v-if="stats && stats.wordSetMastered > 0" class="text-xs text-gray-500 dark:text-gray-400">
          + {{ formatNumber(stats.wordSetMastered) }} mastered from word sets
        </p>
      </div>
    </template>
  </Card>
</template>
