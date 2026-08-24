<script setup lang="ts">
  import type { Poll, PaginatedResponse } from '~/types';
  import { useToast } from 'primevue/usetoast';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({
    title: 'Polls',
    meta: [{ name: 'description', content: 'See currently open polls and past ones.' }],
  });

  const { $api } = useNuxtApp();
  const toast = useToast();

  const PAGE_SIZE = 10;

  const polls = ref<Poll[]>([]);
  const totalItems = ref(0);
  const loading = ref(true);
  const loadingMore = ref(false);

  const activePolls = computed(() => polls.value.filter((p) => !p.isClosed));
  const pastPolls = computed(() => polls.value.filter((p) => p.isClosed));
  const hasMore = computed(() => polls.value.length < totalItems.value);

  async function load(offset: number) {
    const response = await $api<PaginatedResponse<Poll[]>>('polls', { query: { offset, limit: PAGE_SIZE } });
    totalItems.value = response?.totalItems ?? 0;
    return response?.data ?? [];
  }

  onMounted(async () => {
    try {
      polls.value = await load(0);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Could not load polls'), life: 5000 });
    } finally {
      loading.value = false;
    }
  });

  async function loadMore() {
    try {
      loadingMore.value = true;
      polls.value = polls.value.concat(await load(polls.value.length));
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Could not load more polls'), life: 5000 });
    } finally {
      loadingMore.value = false;
    }
  }

  function replacePoll(updated: Poll) {
    const index = polls.value.findIndex((p) => p.id === updated.id);
    if (index !== -1) polls.value[index] = updated;
  }
</script>

<template>
  <div class="flex flex-col gap-4">
    <div>
      <h1 class="text-2xl font-bold">Polls</h1>
      <p class="text-surface-500 dark:text-surface-400">See current open polls and past ones.</p>
    </div>

    <div v-if="loading" class="flex flex-col gap-4">
      <Skeleton v-for="i in 2" :key="i" width="100%" height="10rem" />
    </div>

    <template v-else-if="polls.length === 0">
      <p class="py-8 text-center text-surface-500 dark:text-surface-400">No polls yet.</p>
    </template>

    <template v-else>
      <section v-if="activePolls.length > 0" class="flex flex-col gap-3">
        <h2 class="text-lg font-semibold">Open</h2>
        <PollCard v-for="poll in activePolls" :key="poll.id" :poll="poll" @update:poll="replacePoll" />
      </section>

      <section v-if="pastPolls.length > 0" class="flex flex-col gap-3">
        <h2 class="text-lg font-semibold">Closed</h2>
        <PollCard v-for="poll in pastPolls" :key="poll.id" :poll="poll" @update:poll="replacePoll" />
      </section>

      <div v-if="hasMore" class="flex justify-center">
        <Button label="Load more" outlined :loading="loadingMore" @click="loadMore" />
      </div>
    </template>
  </div>
</template>
