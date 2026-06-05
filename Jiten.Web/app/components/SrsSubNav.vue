<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';

  const srsStore = useSrsStore();
  const route = useRoute();
  const router = useRouter();

  onMounted(() => {
    if (!srsStore.dueSummary) srsStore.fetchDueSummary();
  });

  const tabs = [
    { label: 'Decks', to: '/srs/decks', match: (p: string) => p.startsWith('/srs/decks') },
    { label: 'Cards', to: '/settings/cards', match: (p: string) => p === '/settings/cards' },
    { label: 'History', to: '/srs/history', match: (p: string) => p.startsWith('/srs/history') },
    { label: 'Settings', to: '/settings/srs', match: (p: string) => p === '/settings/srs' },
  ];

  const totalDue = computed(() => {
    const ds = srsStore.dueSummary;
    if (!ds) return 0;
    return Math.min(ds.reviewsDue, ds.reviewBudgetLeft) + ds.newCardsAvailable;
  });

  const studyLabel = computed(() => {
    if (!srsStore.dueSummary) return 'Study …';
    return totalDue.value > 0 ? `Study (${totalDue.value})` : 'Study';
  });

  function startStudy() {
    srsStore.resetSession();
    router.push('/srs/study');
  }
</script>

<template>
  <div class="flex items-center justify-between gap-2 mb-4 border-b border-surface-200 dark:border-surface-700">
    <nav class="flex items-center gap-0.5 sm:gap-1 overflow-x-auto no-scrollbar -mb-px">
      <NuxtLink
        v-for="tab in tabs"
        :key="tab.to"
        :to="tab.to"
        class="px-2.5 sm:px-3 py-2 text-sm whitespace-nowrap border-b-2 transition-colors"
        :class="tab.match(route.path)
          ? 'border-primary-500 font-semibold !text-primary-600 dark:!text-primary-400'
          : 'border-transparent !text-surface-500 dark:!text-surface-400 hover:!text-surface-700 dark:hover:!text-surface-200'"
      >
        {{ tab.label }}
      </NuxtLink>
    </nav>
    <!-- Mobile: icon + count only, so the tab row keeps enough room for all tabs -->
    <Button
      icon="pi pi-play"
      :label="!srsStore.dueSummary ? undefined : totalDue > 0 ? String(totalDue) : undefined"
      :severity="totalDue > 0 ? 'success' : 'secondary'"
      size="small"
      class="sm:!hidden flex-shrink-0 mb-1.5"
      aria-label="Study"
      @click="startStudy"
    />
    <!-- Desktop: full label -->
    <Button
      icon="pi pi-play"
      :label="studyLabel"
      :severity="totalDue > 0 ? 'success' : 'secondary'"
      size="small"
      class="!hidden sm:!inline-flex flex-shrink-0 mb-1.5"
      @click="startStudy"
    />
  </div>
</template>
