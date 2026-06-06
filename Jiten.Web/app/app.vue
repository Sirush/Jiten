<script setup lang="ts">
  useSeoMeta({
    title: 'Jiten',
    description: 'Vocabulary lists and anki decks for all your Japanese media.',
    ogSiteName: 'Jiten',
    ogType: 'website',
    twitterCard: 'summary_large_image',
  });

  useHead({
    titleTemplate: (titleChunk) => {
      return titleChunk ? `${titleChunk} - Jiten` : 'Jiten';
    },
  });

  const route = useRoute();
  const isStudyMode = computed(() => route.path === '/srs/study');
  const studyHeaderVisible = ref(false);

  watch(isStudyMode, () => {
    studyHeaderVisible.value = false;
  });

  provide('studyHeaderVisible', studyHeaderVisible);

  onMounted(() => {
    // Trick from https://github.com/primefaces/primevue/issues/5899#issuecomment-2585781190
    // TODO remove after primevue fix
    document.documentElement.classList.add('loaded');
  });

</script>

<template>
  <div id="app" class="flex flex-col min-h-screen">
    <NuxtLoadingIndicator />
    <ClientOnly>
      <MaintenanceBanner />
    </ClientOnly>

    <div
      class="grid transition-[grid-template-rows] duration-300 ease-in-out"
      :style="{ gridTemplateRows: (!isStudyMode || studyHeaderVisible) ? '1fr' : '0fr' }"
    >
      <div :class="{ 'overflow-hidden': isStudyMode }">
        <AppHeader />
      </div>
    </div>

    <div :class="isStudyMode ? 'flex-grow flex flex-col' : 'container mx-auto pl-4 pr-4 max-w-6xl flex-grow pb-2'">
      <NuxtPage />
    </div>
    <AppFooter v-if="!isStudyMode" />
    <LazyToast />
    <LazyToast position="bottom-center" group="bottom" />
    <LazyConfirmDialog />
  </div>
</template>
