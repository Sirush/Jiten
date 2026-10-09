<script setup lang="ts">
  import Skeleton from 'primevue/skeleton';
  import type { Franchise } from '~/types';
  import { isMissingResource } from '~/utils/missingResource';
  import { toNumOrNull } from '~/utils/queryParams';
  import { franchiseDisplayName } from '~/utils/franchiseLayout';

  definePageMeta({
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const route = useRoute();
  const localiseTitle = useLocaliseTitle();

  const {
    data: franchise,
    status,
    error,
    refresh,
    ready,
  } = useApiFetch<Franchise>(() => `franchise/${route.params.id}`, {
    revalidateOnClient: true,
  });

  // Only a definitive 404 becomes a 404 page: an SSR timeout or 5xx keeps the normal error state at 200.
  if (import.meta.server) {
    await ready;
    if (isMissingResource(error.value, franchise.value)) throw createError({ statusCode: 404, statusMessage: 'Franchise not found', fatal: true });
  }

  const notFound = computed(() => status.value !== 'pending' && isMissingResource(error.value, franchise.value));
  const loaded = computed(() => (franchise.value?.franchiseId != null ? (franchise.value as Franchise & { franchiseId: number }) : null));

  const currentNode = computed(() => {
    const id = toNumOrNull(route.query.deck);
    return id == null ? null : (franchise.value?.nodes.find((n) => n.deckId === id) ?? null);
  });

  const name = computed(() => (franchise.value ? franchiseDisplayName(franchise.value, localiseTitle) : ''));

  const metaDescription = computed(() => {
    const count = franchise.value?.nodes.length ?? 0;
    if (!name.value || count === 0) return '';
    return `All ${count} titles of the ${name.value} franchise, from sequels and adaptations to spin-offs, on a timeline with difficulty ratings and a Japanese vocabulary list merged across all of them.`;
  });

  useSeoMeta({
    title: () => (name.value ? `${name.value} Franchise` : 'Franchise'),
    description: metaDescription,
    ogTitle: () => (name.value ? `${name.value} Franchise: Japanese vocabulary and difficulty` : 'Franchise'),
    ogDescription: metaDescription,
  });
</script>

<template>
  <div class="flex flex-col gap-3">
    <NuxtLink
      v-if="currentNode"
      :to="`/decks/media/${currentNode.deckId}/detail`"
      class="inline-flex max-w-full items-center self-start rounded text-sm text-surface-600! hover:text-primary! focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 max-sm:min-h-11 dark:text-surface-300! dark:hover:text-primary!"
    >
      <span class="truncate">
        ← Back to <span v-bind="japaneseTextAttrs(localiseTitle(currentNode))">{{ localiseTitle(currentNode) }}</span>
      </span>
    </NuxtLink>

    <div v-if="status === 'pending' && !franchise" class="flex flex-col gap-3" aria-busy="true" aria-label="Loading franchise">
      <div class="flex flex-col gap-2">
        <Skeleton width="60%" height="2rem" />
        <Skeleton width="20rem" height="1rem" class="max-w-full" />
      </div>
      <div class="flex flex-wrap gap-2">
        <Skeleton v-for="i in 3" :key="i" width="8rem" height="2.25rem" />
      </div>
      <Skeleton width="100%" height="2.75rem" class="mt-1" />
      <div class="flex flex-row flex-wrap gap-4 pt-2">
        <Skeleton v-for="i in 6" :key="i" width="120px" height="170px" />
      </div>
    </div>

    <div v-else-if="notFound" class="flex flex-col items-center gap-3 py-12 text-center">
      <h1 class="text-xl font-bold">Franchise not found</h1>
      <p class="max-w-md text-surface-600 dark:text-surface-300">There is no franchise with this number. It may have been merged into another one.</p>
      <Button as="router-link" to="/decks/media" label="Browse media" icon="pi pi-search" />
    </div>

    <div v-else-if="error || !loaded" class="flex flex-col items-center gap-4 py-12 text-center">
      <p class="text-surface-600 dark:text-surface-300">This franchise failed to load.</p>
      <Button label="Retry" icon="pi pi-refresh" @click="refresh()" />
    </div>

    <FranchisePage v-else :franchise="loaded" :current-deck-id="currentNode?.deckId ?? null" @renamed="refresh()" />
  </div>
</template>
