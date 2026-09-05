<script setup lang="ts">
  const props = defineProps<{
    /** `row` sits inside the grouped strip card and only shows when there is nothing to vote on; `card` is the full poll and only shows when there is. */
    variant: 'row' | 'card';
  }>();

  const { state, load, onVoted, showNext, skip, canSkip } = useHomePoll();

  onMounted(load);

  const showCard = computed(() => props.variant === 'card' && !state.value.loading && state.value.poll !== null);
  const showRow = computed(() => props.variant === 'row' && !state.value.loading && state.value.poll === null && !state.value.failed);
</script>

<template>
  <div v-if="variant === 'row' && state.loading" class="flex items-start gap-3 p-3">
    <Skeleton shape="circle" width="2rem" height="2rem" />
    <div class="flex-1 flex flex-col gap-2">
      <Skeleton width="5rem" height="0.7rem" />
      <Skeleton width="10rem" height="0.9rem" />
    </div>
  </div>

  <PollCard v-else-if="showCard && state.poll" :poll="state.poll" labeled @update:poll="onVoted">
    <template #footer>
      <div class="flex items-center justify-between gap-2">
        <NuxtLink to="/polls" class="text-sm text-primary-600 dark:text-primary-300 no-underline! hover:underline!">All polls</NuxtLink>
        <Button v-if="state.nextPoll" label="Next poll" icon="pi pi-arrow-right" icon-pos="right" text size="small" @click="showNext" />
        <Button v-else-if="canSkip" label="Skip" text size="small" severity="secondary" :loading="state.skipping" @click="skip" />
      </div>
    </template>
  </PollCard>

  <HomeStrip
    v-else-if="showRow"
    compact
    label="Polls"
    icon="material-symbols:how-to-vote"
    to="/polls"
    :cta="state.allVoted ? 'See results' : 'See past polls'"
  >
    {{ state.allVoted ? "You've voted on every open poll" : 'No open polls right now' }}
  </HomeStrip>
</template>
