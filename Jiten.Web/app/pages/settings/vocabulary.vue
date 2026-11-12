<script setup lang="ts">
  import type { VocabularyOption } from '~/components/VocabularyOptionGrid.vue';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Vocabulary - Settings' });

  const route = useRoute();
  const router = useRouter();

  const {
    vocabStatsLoading,
    vocabStatsRefreshing,
    youngWordsAmount,
    matureWordsAmount,
    masteredWordsAmount,
    blacklistedWordsAmount,
    youngFormsAmount,
    matureFormsAmount,
    masteredFormsAmount,
    blacklistedFormsAmount,
    totalWordsAmount,
    totalFormsAmount,
    wordSetMasteredWords,
    wordSetMasteredForms,
    wordSetBlacklistedWords,
    wordSetBlacklistedForms,
    hasWordSetContributions,
    redundantForms,
    derivationCoveredWords,
    derivationCoveredForms,
    archivedCards,
    fetchKnownWordsAmount,
  } = useVocabularyStats();

  const sections = [
    { id: 'overview', label: 'Overview' },
    { id: 'add', label: 'Import & Export' },
    { id: 'related', label: 'Related Words' },
    { id: 'frequency', label: 'Frequency Ranks' },
    { id: 'cards', label: 'Edit & Restore Cards' },
    { id: 'reset', label: 'Danger Zone' },
  ] as const;

  type SectionId = (typeof sections)[number]['id'];

  const activeSection = ref<SectionId>('overview');
  let scrollFrame = 0;

  function updateActiveSection() {
    scrollFrame = 0;
    const atBottom = window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 2;
    let current: SectionId = atBottom ? sections[sections.length - 1]!.id : sections[0].id;
    if (!atBottom) {
      for (const section of sections) {
        const el = document.getElementById(section.id);
        if (el && el.getBoundingClientRect().top <= 120) current = section.id;
      }
    }
    activeSection.value = current;
  }

  function onScroll() {
    if (!scrollFrame) scrollFrame = requestAnimationFrame(updateActiveSection);
  }

  const navList = ref<HTMLElement | null>(null);
  const navFadeStart = ref(false);
  const navFadeEnd = ref(false);
  let navResizeObserver: ResizeObserver | null = null;

  function updateNavFades() {
    const list = navList.value;
    if (!list) return;
    navFadeStart.value = list.scrollLeft > 1;
    navFadeEnd.value = list.scrollLeft + list.clientWidth < list.scrollWidth - 1;
  }

  watch(activeSection, async () => {
    await nextTick();
    const list = navList.value;
    const link = list?.querySelector<HTMLElement>('[aria-current]');
    if (!list || !link || list.scrollWidth <= list.clientWidth) return;
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    list.scrollTo({ left: link.offsetLeft - (list.clientWidth - link.offsetWidth) / 2, behavior: reducedMotion ? 'auto' : 'smooth' });
  });

  function goTo(id: SectionId | 'export') {
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    document.getElementById(id)?.scrollIntoView({ behavior: reducedMotion ? 'auto' : 'smooth', block: 'start' });
    history.replaceState(history.state, '', `${route.path}${window.location.search}#${id}`);
  }

  const importOptions: VocabularyOption[] = [
    { key: 'anki-connect', label: 'AnkiConnect', desc: 'Import directly from Anki', icon: 'pi pi-sync' },
    { key: 'anki-media', label: 'Anki Sentences & Media', desc: 'Import example sentences, images and audio', icon: 'pi pi-images' },
    { key: 'jpdb', label: 'JPDB', desc: 'Import from JPDB API', icon: 'pi pi-cloud-download' },
    { key: 'anki-file', label: 'Anki File', desc: 'Upload .txt or .csv', icon: 'pi pi-file' },
    { key: 'frequency', label: 'Frequency Range', desc: 'Add words by frequency rank', icon: 'pi pi-chart-bar' },
    { key: 'complete-vocabulary', label: 'Complete Backup', desc: 'Restore a full vocabulary backup', icon: 'pi pi-database' },
  ];

  const option = computed<string | null>({
    get: () => {
      const raw = route.query.option as string | undefined;
      return raw && importOptions.some((o) => o.key === raw) ? raw : null;
    },
    set: (v: string | null) => router.replace({ query: { ...route.query, option: v ?? undefined }, hash: route.hash }),
  });

  const exportOptions: VocabularyOption[] = [
    { key: 'word-list', label: 'Word List', desc: 'Export as text file by state', icon: 'pi pi-file-export' },
    { key: 'complete-vocabulary', label: 'Complete Backup', desc: 'Export full vocabulary backup', icon: 'pi pi-database' },
  ];

  const exportOption = computed<string | null>({
    get: () => {
      const raw = route.query.export as string | undefined;
      return raw && exportOptions.some((o) => o.key === raw) ? raw : null;
    },
    set: (v: string | null) => router.replace({ query: { ...route.query, export: v ?? undefined }, hash: route.hash }),
  });

  async function openBackupExport() {
    await router.replace({ query: { ...route.query, export: 'complete-vocabulary' } });
    await nextTick();
    goTo('export');
  }

  onMounted(async () => {
    window.addEventListener('scroll', onScroll, { passive: true });
    updateActiveSection();
    if (navList.value) {
      navResizeObserver = new ResizeObserver(updateNavFades);
      navResizeObserver.observe(navList.value);
    }

    if (route.query.mode === 'export') {
      const legacyOption = route.query.option === 'complete-vocabulary' ? 'complete-vocabulary' : 'word-list';
      await router.replace({ query: { export: legacyOption } });
      await nextTick();
      goTo('export');
    }

    await fetchKnownWordsAmount();
  });

  onBeforeUnmount(() => {
    window.removeEventListener('scroll', onScroll);
    cancelAnimationFrame(scrollFrame);
    navResizeObserver?.disconnect();
  });

  const importHistoryReloadKey = ref(0);

  const { refresh: refreshOnboarding } = useOnboarding();

  function onHistoryChanged() {
    fetchKnownWordsAmount();
    refreshOnboarding().catch(() => {});
  }

  function onPanelChanged() {
    fetchKnownWordsAmount();
    importHistoryReloadKey.value++;
    refreshOnboarding().catch(() => {});
  }
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <div class="flex items-center gap-2 mb-6">
      <Button as="router-link" to="/settings" aria-label="Back to settings" icon="pi pi-arrow-left" severity="secondary" text rounded />
      <h1 class="text-2xl font-bold">Vocabulary</h1>
    </div>

    <div class="vocabulary-layout">
      <nav aria-label="Vocabulary settings sections" class="vocabulary-nav">
        <div
          ref="navList"
          class="vocabulary-nav__list no-scrollbar"
          :class="{ 'vocabulary-nav__list--fade-start': navFadeStart, 'vocabulary-nav__list--fade-end': navFadeEnd }"
          @scroll.passive="updateNavFades"
        >
          <a
            v-for="section in sections"
            :key="section.id"
            :href="`#${section.id}`"
            :aria-current="activeSection === section.id ? 'location' : undefined"
            class="vocabulary-nav__link"
            :class="{ 'vocabulary-nav__link--danger': section.id === 'reset' }"
            @click.prevent="goTo(section.id)"
          >
            {{ section.label }}
          </a>
        </div>
      </nav>

      <div class="flex min-w-0 flex-col gap-10">
        <section id="overview" class="vocabulary-section" aria-label="Overview">
          <SettingsVocabularyStatsCard
            :vocab-stats-loading="vocabStatsLoading"
            :refreshing="vocabStatsRefreshing"
            :young-words-amount="youngWordsAmount"
            :mature-words-amount="matureWordsAmount"
            :mastered-words-amount="masteredWordsAmount"
            :blacklisted-words-amount="blacklistedWordsAmount"
            :young-forms-amount="youngFormsAmount"
            :mature-forms-amount="matureFormsAmount"
            :mastered-forms-amount="masteredFormsAmount"
            :blacklisted-forms-amount="blacklistedFormsAmount"
            :total-words-amount="totalWordsAmount"
            :total-forms-amount="totalFormsAmount"
            :word-set-mastered-words="wordSetMasteredWords"
            :word-set-mastered-forms="wordSetMasteredForms"
            :word-set-blacklisted-words="wordSetBlacklistedWords"
            :word-set-blacklisted-forms="wordSetBlacklistedForms"
            :has-word-set-contributions="hasWordSetContributions"
            :redundant-forms="redundantForms"
            :derivation-covered-words="derivationCoveredWords"
            :derivation-covered-forms="derivationCoveredForms"
          />
        </section>

        <section id="add" class="vocabulary-section" aria-labelledby="vocabulary-add">
          <h2 id="vocabulary-add" class="vocabulary-group-label">Import & Export</h2>
          <div class="vocabulary-group">
            <SettingsSetupCard />
            <SettingsListItem title="Import">
              <VocabularyOptionGrid v-model="option" :options="importOptions" />
              <div v-if="option" class="embedded-panel mt-5 border-t border-surface-200 pt-5 dark:border-surface-700">
                <VocabularyImportAnkiConnectPanel v-if="option === 'anki-connect'" @changed="onPanelChanged" />
                <VocabularyImportAnkiExtrasPanel v-if="option === 'anki-media'" @changed="onPanelChanged" />
                <VocabularyImportJpdbPanel v-if="option === 'jpdb'" @changed="onPanelChanged" />
                <VocabularyImportAnkiFilePanel v-if="option === 'anki-file'" @changed="onPanelChanged" />
                <VocabularyImportFrequencyPanel v-if="option === 'frequency'" @changed="onPanelChanged" />
                <VocabularyCompleteVocabularyPanel v-if="option === 'complete-vocabulary'" mode="import" @changed="onPanelChanged" />
              </div>
            </SettingsListItem>
            <SettingsImportHistoryCard :reload-key="importHistoryReloadKey" @changed="onHistoryChanged" />
            <SettingsListItem id="export" title="Export" class="vocabulary-section">
              <VocabularyOptionGrid v-model="exportOption" :options="exportOptions" />
              <div v-if="exportOption" class="embedded-panel mt-5 border-t border-surface-200 pt-5 dark:border-surface-700">
                <VocabularyExportWordListPanel v-if="exportOption === 'word-list'" @changed="onPanelChanged" />
                <VocabularyCompleteVocabularyPanel v-if="exportOption === 'complete-vocabulary'" mode="export" @changed="onPanelChanged" />
              </div>
            </SettingsListItem>
            <SettingsListItem title="Word Sets" description="Master or blacklist whole categories like names or particles.">
              <template #action>
                <NuxtLink to="/settings/word-sets" class="text-sm font-medium">Manage Word Sets</NuxtLink>
              </template>
            </SettingsListItem>
          </div>
        </section>

        <section id="related" class="vocabulary-section" aria-labelledby="vocabulary-related">
          <h2 id="vocabulary-related" class="vocabulary-group-label">Related Words</h2>
          <div class="vocabulary-group">
            <SettingsDerivationalRedundancyCard @changed="onPanelChanged" />
            <SettingsCompositionInferenceCard @changed="onPanelChanged" />
          </div>
        </section>

        <section id="frequency" class="vocabulary-section" aria-labelledby="vocabulary-frequency">
          <h2 id="vocabulary-frequency" class="vocabulary-group-label">Frequency Ranks</h2>
          <div class="vocabulary-group">
            <SettingsFrequencySourceCard />
          </div>
        </section>

        <section id="cards" class="vocabulary-section" aria-labelledby="vocabulary-cards">
          <h2 id="vocabulary-cards" class="vocabulary-group-label">Edit & Restore Cards</h2>
          <div class="vocabulary-group">
            <SettingsRedundantFormsCard @changed="onPanelChanged" />
            <SettingsRecentlyRemovedCard :archived-count="archivedCards" :count-loading="vocabStatsLoading" @changed="onPanelChanged" />
            <SettingsVocabularyMassActionsCard @changed="onPanelChanged" />
          </div>
        </section>

        <section id="reset" class="vocabulary-section" aria-labelledby="vocabulary-reset">
          <h2 id="vocabulary-reset" class="vocabulary-group-label vocabulary-group-label--danger">Danger Zone</h2>
          <div class="vocabulary-group">
            <SettingsVocabularyDangerZoneCard @changed="onPanelChanged" @backup="openBackupExport" />
          </div>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
  .vocabulary-group-label {
    margin-bottom: 0.875rem;
    font-size: 1.25rem;
    font-weight: 700;
  }

  .vocabulary-group-label--danger {
    color: var(--p-red-700);
  }

  .vocabulary-group {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .vocabulary-layout {
    position: relative;
  }

  .vocabulary-section {
    scroll-margin-top: 3.5rem;
  }

  .vocabulary-nav {
    position: sticky;
    top: 0;
    z-index: 10;
    margin-bottom: 0.5rem;
    padding-block: 0.5rem;
    background: var(--jiten-page-bg);
  }

  .vocabulary-nav__list {
    --fade-start: 0rem;
    --fade-end: 0rem;
    position: relative;
    display: flex;
    gap: 0.25rem;
    overflow-x: auto;
    mask-image: linear-gradient(to right, transparent, #000 var(--fade-start), #000 calc(100% - var(--fade-end)), transparent);
  }

  .vocabulary-nav__list--fade-start {
    --fade-start: 2.5rem;
  }

  .vocabulary-nav__list--fade-end {
    --fade-end: 2.5rem;
  }

  /* Container width plus a 12rem rail and its gap on both sides, so the rail never overlaps the page edge. */
  @media (min-width: 1580px) {
    .vocabulary-section {
      scroll-margin-top: 1rem;
    }

    .vocabulary-nav {
      position: absolute;
      top: 0;
      right: 100%;
      width: 12rem;
      height: 100%;
      margin: 0 1.5rem 0 0;
      padding: 0;
      background: none;
    }

    .vocabulary-nav__list {
      position: sticky;
      top: 1rem;
      flex-direction: column;
      overflow: visible;
      mask-image: none;
    }
  }

  .vocabulary-nav__link {
    flex-shrink: 0;
    padding: 0.4rem 0.65rem;
    border-radius: var(--radius-lg);
    font-size: 0.875rem;
    white-space: nowrap;
    text-decoration: none;
    color: var(--p-surface-600);
  }

  .vocabulary-nav__link:hover {
    background: var(--p-surface-100);
    color: var(--p-surface-900);
  }

  .vocabulary-nav__link[aria-current] {
    background: var(--p-primary-50);
    color: var(--p-primary-700);
    font-weight: 600;
  }

  .vocabulary-nav__link--danger:not([aria-current]) {
    color: var(--p-red-700);
  }

  .embedded-panel :deep(.p-card) {
    border: 0;
    background: transparent;
    box-shadow: none;
  }

  .embedded-panel :deep(.p-card-body) {
    padding: 0;
  }

  .embedded-panel :deep(.p-card-title) {
    display: none;
  }
</style>

<style>
  .dark-mode .vocabulary-group-label--danger {
    color: var(--p-red-400);
  }

  .dark-mode .vocabulary-nav a.vocabulary-nav__link {
    color: var(--p-surface-300);
  }

  .dark-mode .vocabulary-nav a.vocabulary-nav__link:hover {
    background: var(--p-surface-800);
    color: var(--p-surface-0);
  }

  .dark-mode .vocabulary-nav a.vocabulary-nav__link[aria-current] {
    background: var(--p-primary-950);
    color: var(--p-primary-200);
  }

  .dark-mode .vocabulary-nav a.vocabulary-nav__link--danger:not([aria-current]) {
    color: var(--p-red-400);
  }
</style>
