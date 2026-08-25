<script setup lang="ts">
  import type { Poll } from '~/types';

  const { $api } = useNuxtApp();

  const poll = ref<Poll | null>(null);
  const nextPoll = ref<Poll | null>(null);
  const allVoted = ref(false);
  const loading = ref(true);
  const failed = ref(false);
  const skipping = ref(false);
  const skipped = ref<number[]>([]);

  async function fetchHomePoll() {
    const query = skipped.value.length > 0 ? { exclude: skipped.value } : undefined;
    return (await $api<Poll | null>('polls/home', { query })) ?? null;
  }

  function applyFetched(fetched: Poll | null) {
    // Backstop for servers that ignore the exclude param
    if (fetched && fetched.myOptionIds.length === 0 && skipped.value.includes(fetched.id)) {
      allVoted.value = false;
      poll.value = null;
      return;
    }
    // polls/home returns a voted poll only when no unvoted one is left
    if (fetched && fetched.myOptionIds.length > 0) {
      allVoted.value = true;
      poll.value = null;
    } else {
      allVoted.value = false;
      poll.value = fetched;
    }
  }

  onMounted(async () => {
    skipped.value = readSkippedPollIds();
    try {
      applyFetched(await fetchHomePoll());
    } catch {
      failed.value = true;
    } finally {
      loading.value = false;
    }
  });

  async function onVoted(updated: Poll) {
    poll.value = updated;
    try {
      const fetched = await fetchHomePoll();
      nextPoll.value = fetched && fetched.id !== updated.id && fetched.myOptionIds.length === 0 ? fetched : null;
    } catch {
      nextPoll.value = null;
    }
  }

  function showNext() {
    if (!nextPoll.value) return;
    poll.value = nextPoll.value;
    nextPoll.value = null;
  }

  const canSkip = computed(() => poll.value !== null && poll.value.myOptionIds.length === 0);

  async function skip() {
    if (!poll.value || skipping.value) return;
    skipped.value = recordSkippedPollId(poll.value.id);
    try {
      skipping.value = true;
      applyFetched(await fetchHomePoll());
    } catch {
      poll.value = null;
    } finally {
      skipping.value = false;
    }
  }
</script>

<template>
  <div v-if="loading" class="flex items-start gap-3 p-4 rounded-xl border border-surface-200 dark:border-surface-700">
    <Skeleton shape="circle" width="2.25rem" height="2.25rem" />
    <div class="flex-1 flex flex-col gap-2">
      <Skeleton width="5rem" height="0.7rem" />
      <Skeleton width="10rem" height="0.9rem" />
    </div>
  </div>

  <PollCard v-else-if="poll" :poll="poll" labeled @update:poll="onVoted">
    <template #footer>
      <div class="flex items-center justify-between gap-2">
        <NuxtLink to="/polls" class="text-sm text-primary-600 dark:text-primary-300 no-underline! hover:underline!"> All polls </NuxtLink>
        <Button v-if="nextPoll" label="Next poll" icon="pi pi-arrow-right" icon-pos="right" text size="small" @click="showNext" />
        <Button v-else-if="canSkip" label="Skip" text size="small" severity="secondary" :loading="skipping" @click="skip" />
      </div>
    </template>
  </PollCard>

  <HomeStrip v-else-if="!failed" label="Polls" icon="material-symbols:how-to-vote" to="/polls" :cta="allVoted ? 'See results' : 'See past polls'">
    {{ allVoted ? "You've voted on every open poll" : 'No open polls right now' }}
  </HomeStrip>
</template>
