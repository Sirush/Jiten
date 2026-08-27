<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';

  const srsStore = useSrsStore();
  const { loaded, totalDue, goalReviewsDone, goalReviewsTarget, goalNewDone, goalNewTarget, nextReviewText, hasStudyDecks, startStudy } = useStudySummary();

  const ready = ref(false);

  onMounted(async () => {
    if (!srsStore.reviewForecast30d) srsStore.fetchReviewForecast30d();
    await srsStore.refreshStudySummary();
    ready.value = true;
  });

  const streak = computed(() => srsStore.deckStreak?.currentStreak ?? 0);
  const dueTomorrow = computed(() => srsStore.reviewForecast30d?.days?.[1]?.count ?? 0);
</script>

<template>
  <div
    v-if="!ready || !loaded"
    class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-5 flex flex-col sm:flex-row items-center gap-5"
  >
    <Skeleton shape="circle" size="96px" class="shrink-0" />
    <div class="flex flex-col items-center sm:items-start gap-2 flex-1">
      <Skeleton width="10rem" height="1.5rem" />
      <Skeleton width="6rem" height="0.75rem" />
    </div>
    <Skeleton width="10rem" height="3rem" class="shrink-0" />
  </div>

  <!-- A user who has never added a study deck gets a one-line invitation, not an empty 0/0 ring. -->
  <HomeStrip v-else-if="!hasStudyDecks" label="Study" icon="material-symbols-light:school" to="/srs/decks">
    Using Jiten's SRS? Add your first deck to start reviewing.
  </HomeStrip>

  <div v-else class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-5">
    <div class="flex flex-col sm:flex-row sm:flex-wrap items-center gap-5 sm:gap-y-4">
      <GoalRing
        class="shrink-0"
        :reviews-done="goalReviewsDone"
        :reviews-target="goalReviewsTarget"
        :new-done="goalNewDone"
        :new-target="goalNewTarget"
        :total-to-study="totalDue"
      />

      <!-- The ring already carries the counts; this column only adds what it cannot show. -->
      <div class="flex-1 flex flex-col items-center sm:items-start gap-1">
        <div v-if="totalDue === 0" class="text-xl font-bold">All caught up</div>

        <div class="flex flex-wrap items-center justify-center sm:justify-start gap-x-4 gap-y-1 text-sm text-gray-500 dark:text-gray-400">
          <span v-if="streak > 0" class="inline-flex items-center gap-1 whitespace-nowrap">
            <Icon name="material-symbols:local-fire-department-rounded" class="text-orange-500" size="1.15em" />
            <span class="font-semibold text-gray-700 dark:text-gray-300 tabular-nums">{{ streak }}</span>
            day streak
          </span>
          <span v-if="totalDue > 0 && nextReviewText" class="inline-flex items-center gap-1 whitespace-nowrap">
            <Icon name="material-symbols:schedule-outline-rounded" size="1.15em" />
            Next in {{ nextReviewText }}
          </span>
          <span v-else-if="totalDue === 0 && dueTomorrow > 0" class="inline-flex items-center gap-1 whitespace-nowrap">
            <Icon name="material-symbols:schedule-outline-rounded" size="1.15em" />
            <span class="font-semibold text-gray-700 dark:text-gray-300 tabular-nums">{{ dueTomorrow }}</span>
            due tomorrow
          </span>
        </div>
      </div>

      <div class="flex flex-col-reverse sm:flex-row gap-2 w-full sm:w-auto sm:shrink-0">
        <Button as="router-link" to="/srs/decks" label="Study decks" icon="pi pi-list" severity="secondary" outlined size="large" class="justify-center" />
        <Button
          :label="totalDue > 0 ? 'Start studying' : 'Study ahead'"
          icon="pi pi-play"
          :severity="totalDue > 0 ? 'success' : 'secondary'"
          size="large"
          class="justify-center"
          @click="startStudy"
        />
      </div>
    </div>
  </div>
</template>
