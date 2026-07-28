<script setup lang="ts">
  import type { KnowledgeGrowth } from '~/types';

  const emit = defineEmits<{
    loaded: [growth: KnowledgeGrowth | null];
  }>();

  const { $api } = useNuxtApp();

  const isLoading = ref(true);
  const growth = ref<KnowledgeGrowth | null>(null);

  const load = async () => {
    isLoading.value = true;
    try {
      growth.value = await $api<KnowledgeGrowth>('srs/knowledge-growth');
    } catch {
      growth.value = null;
    } finally {
      isLoading.value = false;
      emit('loaded', growth.value);
    }
  };

  onMounted(() => load());

  const hasData = computed(() => Boolean(growth.value?.hasEnoughHistory) && (growth.value?.points.length ?? 0) > 1);

  const points = computed(() => growth.value?.points ?? []);
  const learned = computed(() => points.value[points.value.length - 1]?.knownWords ?? 0);
  const gained = computed(() => learned.value - (points.value[0]?.knownWords ?? 0));
  const startLabel = computed(() => (points.value.length ? formatBucketDated(points.value[0]!.date, growth.value!.granularity) : ''));
</script>

<template>
  <Card v-if="isLoading || hasData">
    <template #title>
      <div class="flex items-center gap-2">
        <Icon name="material-symbols:trending-up" />
        Words Learned Over Time
      </div>
    </template>
    <template #content>
      <div v-if="isLoading" class="flex flex-col gap-3">
        <Skeleton width="12rem" height="2.25rem" />
        <Skeleton width="100%" height="7rem" />
      </div>

      <div v-else class="flex flex-col gap-3">
        <div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
          <span class="text-[clamp(1.5rem,6vw,2.25rem)] font-bold tabular-nums text-primary-600 dark:text-primary-300">
            {{ learned.toLocaleString() }}
          </span>
          <span class="text-gray-500">{{ learned === 1 ? 'word learned' : 'words learned' }}</span>
          <span v-if="gained > 0" class="text-sm font-semibold text-green-600 dark:text-green-400">
            +{{ gained.toLocaleString() }} since {{ startLabel }}
          </span>
        </div>

        <CoverageJourneyChart :points="points" :granularity="growth!.granularity" mode="count" compact height="140px" />

        <div class="flex justify-between text-xs text-gray-400">
          <span>{{ startLabel }}</span>
          <span>Today</span>
        </div>
      </div>
    </template>
  </Card>
</template>
