<script setup lang="ts">
  useSeoMeta({
    title: 'Jiten',
    description: 'Vocabulary lists and anki decks for all your Japanese media.',
  });

  useHead({
    titleTemplate: (titleChunk) => {
      return titleChunk ? `${titleChunk} - Jiten` : 'Jiten';
    },
  });

  onMounted(() => {
    // Trick from https://github.com/primefaces/primevue/issues/5899#issuecomment-2585781190
    // TODO remove after primevue fix
    document.documentElement.classList.add('loaded');
  });
</script>

<template>
  <div id="app" class="flex flex-col min-h-screen">
    <NuxtLoadingIndicator />
    <AppHeader />
    <div class="container mx-auto pl-4 pr-4 max-w-6xl flex-grow pb-2">
      <NuxtPage />
    </div>
    <AppFooter />
  </div>
</template>
