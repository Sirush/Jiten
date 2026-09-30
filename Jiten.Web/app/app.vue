<script setup lang="ts">
  useSeoMeta({
    title: 'Jiten',
    description: 'Vocabulary lists and anki decks for all your Japanese media.',
    ogSiteName: 'Jiten',
    ogType: 'website',
    twitterCard: 'summary_large_image',
  });

  if (import.meta.server) {
    defineOgImage('PageOgImage', {
      title: 'Immerse yourself in Japanese media you can understand',
      description: 'The open Japanese immersion platform: choose native media at your level, measure your knowledge and study the vocabulary you need.',
    });
  }

  const jitenStore = useJitenStore();
  // On <html> so teleported popovers and dialogs pick up the Japanese font, furigana size and motion choice too; read from cookies, so SSR paints them.
  const displayClasses = computed(() =>
    [
      jitenStore.japaneseFont !== 'default' ? `font-ja-${jitenStore.japaneseFont}` : '',
      jitenStore.japaneseFontWordsOnly ? 'font-ja-words-only' : '',
      jitenStore.japaneseFontDictionaries ? 'font-ja-dictionaries' : '',
      jitenStore.reducedMotion === 'always' ? 'reduce-motion' : '',
      jitenStore.furiganaSize !== 'md' ? `furigana-${jitenStore.furiganaSize}` : '',
    ]
      .filter(Boolean)
      .join(' ')
  );

  const customFontStyle = computed(() => {
    const family = jitenStore.japaneseFont === 'custom' ? customFontFamily(jitenStore.japaneseCustomFont) : null;
    return family ? `--jiten-font-custom: ${family}` : undefined;
  });

  if (import.meta.client) watch(() => jitenStore.japaneseFont, loadJapaneseFont, { immediate: true });

  useHead({
    titleTemplate: (titleChunk) => {
      return titleChunk ? `${titleChunk} - Jiten` : 'Jiten';
    },
    htmlAttrs: { class: displayClasses, style: customFontStyle },
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
  <div id="app" class="flex flex-col min-h-screen overflow-x-clip">
    <NuxtLoadingIndicator />
    <ClientOnly>
      <MaintenanceBanner />
      <UpdateAvailablePrompt v-if="isStudyMode" inline />
    </ClientOnly>

    <div
      class="grid transition-[grid-template-rows] duration-300 ease-in-out"
      :style="{ gridTemplateRows: !isStudyMode || studyHeaderVisible ? '1fr' : '0fr' }"
    >
      <div :class="{ 'overflow-hidden': isStudyMode }">
        <AppHeader />
      </div>
    </div>

    <div :class="isStudyMode ? 'flex-grow flex flex-col' : ['container mx-auto pl-4 pr-4 flex-grow pb-2', route.meta.wide ? 'max-w-7xl' : 'max-w-6xl']">
      <!-- On the home page the banner renders inside HomeMember instead, below the study summary. -->
      <ClientOnly>
        <LegalUpdateBanner v-if="!isStudyMode && route.path !== '/'" class="mt-2 mb-3" />
      </ClientOnly>
      <NuxtPage />
    </div>
    <AppFooter v-if="!isStudyMode" />
    <LazyGuidesSearch />
    <LazyToast />
    <LazyToast position="bottom-center" group="bottom" />
    <LazyConfirmDialog />
    <ClientOnly>
      <UpdateAvailablePrompt v-if="!isStudyMode" />
    </ClientOnly>
  </div>
</template>
