<script setup lang="ts">
  import type { Franchise } from '~/types';
  import Skeleton from 'primevue/skeleton';
  import Button from 'primevue/button';
  import { SelectButton } from 'primevue';
  import { useApiFetch } from '~/composables/useApiFetch';

  const route = useRoute();
  const router = useRouter();
  const deckId = computed(() => Number(route.params.id));
  const localiseTitle = useLocaliseTitle();

  // View toggle synced to ?view=web (timeline default). router.replace keeps toggling out of history.
  type FranchiseView = 'timeline' | 'web';
  const viewOptions = [
    { label: 'Timeline', value: 'timeline' },
    { label: 'Web', value: 'web' },
  ];
  const view = computed<FranchiseView>({
    get: () => (route.query.view === 'web' ? 'web' : 'timeline'),
    set: (v) => {
      const query = { ...route.query };
      if (v === 'web') query.view = 'web';
      else delete query.view;
      router.replace({ query });
    },
  });

  const {
    data: franchise,
    status,
    error,
    refresh,
  } = await useApiFetch<Franchise>(() => `media-deck/${route.params.id}/franchise`, {
    revalidateOnClient: true,
  });

  const hasFranchise = computed(() => (franchise.value?.nodes.length ?? 0) >= 2);
  const currentNode = computed(() => franchise.value?.nodes.find((n) => n.deckId === deckId.value));
  const title = computed(() => (currentNode.value ? localiseTitle(currentNode.value) : ''));

  useSeoMeta({
    title: () => (title.value ? `${title.value} Franchise — Related Media Timeline` : 'Franchise'),
    description: () =>
      title.value
        ? `All ${franchise.value?.nodes.length ?? ''} related media for ${title.value} — sequels, adaptations and spin-offs on a timeline with difficulty ratings.`
        : '',
  });
</script>

<template>
  <div>
    <NuxtLink :to="`/decks/media/${deckId}/detail`" class="text-primary text-sm">
      ← Back to {{ title || 'deck' }}
    </NuxtLink>

    <div v-if="status === 'pending'" class="pt-4">
      <Skeleton width="40%" height="2rem" />
      <div class="flex flex-row flex-wrap gap-4 pt-4">
        <Skeleton v-for="i in 6" :key="i" width="120px" height="170px" />
      </div>
    </div>

    <div v-else-if="error" class="flex flex-col items-center gap-4 py-12 text-center">
      <p class="text-surface-500">Failed to load this franchise.</p>
      <Button label="Retry" icon="pi pi-refresh" @click="refresh()" />
    </div>

    <template v-else-if="franchise">
      <div class="flex flex-wrap items-baseline justify-between gap-x-2 gap-y-2 pt-2">
        <div class="flex flex-wrap items-baseline gap-x-2">
          <h1 class="text-xl font-bold">{{ title }} <span class="font-normal">Franchise</span></h1>
          <span class="text-sm text-gray-500 dark:text-gray-400">
            {{ franchise.nodes.length }} entries
            <template v-if="franchise.truncated">(showing first {{ franchise.nodes.length }})</template>
          </span>
        </div>
        <SelectButton
          v-if="hasFranchise"
          v-model="view"
          :options="viewOptions"
          option-value="value"
          option-label="label"
          :allow-empty="false"
          :pt="{ button: { class: 'text-sm py-1 px-3' } }"
        />
      </div>

      <template v-if="hasFranchise">
        <FranchiseWeb v-if="view === 'web'" :franchise="franchise" :current-deck-id="deckId" />
        <FranchiseTimeline v-else :franchise="franchise" :current-deck-id="deckId" />
      </template>
      <p v-else class="pt-4 text-surface-500">No related media found for this title.</p>
    </template>
  </div>
</template>

<style scoped></style>
