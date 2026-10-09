<script setup lang="ts">
  import { type MediaGroupMembers, MediaGroupKind } from '~/types';
  import { isMissingResource } from '~/utils/missingResource';
  import { franchiseFirstNode } from '~/utils/franchiseLayout';
  import { franchisePath } from '~/utils/mediaGroup';

  definePageMeta({
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const route = useRoute();
  const seriesId = Number(route.params.id);

  const {
    data: group,
    error,
    ready,
  } = useApiFetch<MediaGroupMembers>('media-group/members', {
    query: { kind: MediaGroupKind.Series, id: seriesId },
  });
  await ready;

  if (isMissingResource(error.value, group.value)) throw createError({ statusCode: 404, statusMessage: 'Series not found', fatal: true });

  const franchiseId = group.value?.franchiseId ?? null;

  if (group.value && franchiseId != null) {
    await navigateTo(franchisePath(franchiseId, { kind: MediaGroupKind.Series, id: seriesId }), { redirectCode: 301, replace: true });
  } else if (group.value) {
    const first = franchiseFirstNode(group.value.members);
    if (!first) throw createError({ statusCode: 404, statusMessage: 'Series not found', fatal: true });
    await navigateTo(`/decks/media/${first.deckId}/detail`, { redirectCode: 302, replace: true });
  }

  useSeoMeta({ title: 'Series', robots: 'noindex, follow' });
</script>

<template>
  <div class="flex flex-col items-center gap-4 py-12 text-center">
    <p class="text-surface-600 dark:text-surface-300">This series failed to load.</p>
    <Button label="Retry" icon="pi pi-refresh" @click="reloadNuxtApp()" />
  </div>
</template>
