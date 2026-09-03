<script setup lang="ts">
  import MediaList from '~/components/MediaList.vue';
  import { buildMediaListMeta } from '~/utils/mediaListMeta';

  const route = useRoute();
  const meta = computed(() => buildMediaListMeta(route.query as Record<string, unknown>));

  useHead({ title: () => meta.value?.title ?? 'Media decks' });

  const shared = meta.value;
  if (shared) {
    useSeoMeta({
      description: shared.description,
      ogTitle: shared.title,
      ogDescription: shared.description,
      twitterTitle: shared.title,
      twitterDescription: shared.description,
    });
    defineOgImage('PageOgImage', {
      title: shared.title,
      category: 'Media browser',
      description: shared.summary,
    });
  }
</script>

<template>
  <MediaList />
</template>

<style scoped></style>
