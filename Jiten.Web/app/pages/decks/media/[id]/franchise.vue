<script setup lang="ts">
  import { isMissingResource } from '~/utils/missingResource';

  definePageMeta({
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const route = useRoute();
  const deckId = Number(route.params.id);

  const { data: deck, error, ready } = useApiFetch<{ franchiseId: number | null }>(`media-deck/${deckId}/franchise`);
  await ready;

  if (isMissingResource(error.value, deck.value)) throw createError({ statusCode: 404, statusMessage: 'Media not found', fatal: true });

  if (deck.value?.franchiseId != null) {
    await navigateTo({ path: `/franchise/${deck.value.franchiseId}`, query: { ...route.query, deck: String(deckId) } }, { redirectCode: 301, replace: true });
  } else if (deck.value) {
    await navigateTo(`/decks/media/${deckId}/detail`, { redirectCode: 302, replace: true });
  }

  useSeoMeta({ title: 'Franchise', robots: 'noindex, follow' });
</script>

<template>
  <div class="flex flex-col items-center gap-4 py-12 text-center">
    <p class="text-surface-600 dark:text-surface-300">This franchise failed to load.</p>
    <Button label="Retry" icon="pi pi-refresh" @click="reloadNuxtApp()" />
  </div>
</template>
