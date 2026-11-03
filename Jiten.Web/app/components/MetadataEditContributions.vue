<script setup lang="ts">
  import type { MetadataContributions } from '~/types/metadataEdits';
  import { useApiFetch } from '~/composables/useApiFetch';

  const props = defineProps<{ userId: string }>();

  const { data, status, error, refresh } = useApiFetch<MetadataContributions>(() => `metadata-edits/contributions/${props.userId}`, {
    server: false,
    lazy: true,
  });

  const stats = computed(() => {
    const d = data.value;
    if (!d) return [];
    return [
      { label: 'Accepted edits', value: d.accepted },
      { label: 'Decks improved', value: d.decksImproved },
      { label: 'Edits checked', value: d.checked },
      ...(d.pending != null ? [{ label: 'Waiting for review', value: d.pending }] : []),
    ];
  });
</script>

<template>
  <section
    class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 p-4 flex flex-col gap-3"
    aria-labelledby="metadata-edits-heading"
  >
    <div class="flex flex-wrap items-baseline justify-between gap-2">
      <h2 id="metadata-edits-heading" class="m-0 text-base font-bold">Metadata edits</h2>
      <span class="flex gap-3 text-sm">
        <NuxtLink to="/review">Review queue</NuxtLink>
        <NuxtLink to="/changes">Recent changes</NuxtLink>
      </span>
    </div>

    <div v-if="error && !data" class="flex items-center gap-2 text-sm">
      <span>Your edit stats didn't load.</span>
      <Button label="Retry" size="small" text @click="refresh()" />
    </div>
    <dl v-else class="m-0 grid grid-cols-2 sm:grid-cols-4 gap-3" :aria-busy="status === 'pending'">
      <template v-if="data">
        <div v-for="stat in stats" :key="stat.label" class="flex flex-col gap-0.5">
          <dt class="text-xs text-muted-color">{{ stat.label }}</dt>
          <dd class="m-0 text-2xl font-bold tabular-nums">{{ stat.value.toLocaleString() }}</dd>
        </div>
      </template>
      <template v-else>
        <Skeleton v-for="i in 4" :key="i" height="3.25rem" />
      </template>
    </dl>
    <p class="m-0 text-xs text-muted-color">
      Spot a wrong title, date or link on a deck? Use Suggest an edit from the deck's menu. Accounts with a record of accepted edits can skip review for simple
      fixes.
    </p>
  </section>
</template>
