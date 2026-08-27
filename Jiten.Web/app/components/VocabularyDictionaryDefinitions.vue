<script setup lang="ts">
  import type { Reading } from '~/types';
  import type { ResolvedDefinitionGroup } from '~/composables/useYomitanDictionary';
  import { definitionsToHtml } from '~/composables/useYomitanDictionary';

  const props = withDefaults(
    defineProps<{
      resolvedGroups: readonly ResolvedDefinitionGroup[];
      isCompact: boolean;
      currentReadingIndex?: number;
      readings?: Reading[];
      arrowKeyNav?: boolean;
      maxDefinitions?: number | null;
      wordId?: number;
      hiddenBehaviour?: 'gray' | 'hide';
      fontControls?: boolean;
    }>(),
    { arrowKeyNav: true, maxDefinitions: null, fontControls: true }
  );

  const store = useJitenStore();

  const singleJmDictOnly = computed(() => props.resolvedGroups.length === 1 && props.resolvedGroups[0].isJmDict);

  const hasMultipleGroups = computed(() => props.resolvedGroups.length > 1);
  const customFontStyle = computed(() => ({ fontSize: `${store.customDictionaryFontSize}px` }));
  const visibleGroupCount = computed(() => props.resolvedGroups.length);
  const activeTab = ref<string | undefined>(undefined);

  const activeGroupIsCustom = computed(() => {
    const active = props.resolvedGroups.find((g) => g.dictionaryId === activeTab.value);
    return !!active && !active.isJmDict;
  });

  watch(
    () => props.resolvedGroups,
    (groups) => {
      if (groups.length === 0) return;
      if (!activeTab.value || !groups.some((g) => g.dictionaryId === activeTab.value)) {
        const preferred = groups.find((g) => g.dictionaryId === store.preferredDictionaryId);
        activeTab.value = (preferred ?? groups[0]!).dictionaryId;
      }
    },
    { immediate: true }
  );

  function onTabSelected(value: string | number) {
    store.preferredDictionaryId = String(value);
  }

  function cycleDictionary(direction: 1 | -1) {
    const groups = props.resolvedGroups;
    if (props.isCompact || groups.length < 2) return;
    const current = groups.findIndex((g) => g.dictionaryId === activeTab.value);
    const next = ((current === -1 ? 0 : current) + direction + groups.length) % groups.length;
    activeTab.value = groups[next]!.dictionaryId;
    store.preferredDictionaryId = activeTab.value;
  }

  function onWindowKeydown(e: KeyboardEvent) {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    if (e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
    const target = e.target as HTMLElement | null;
    // Don't steal arrows from text inputs, or double-handle PrimeVue's own tab-header navigation.
    if (target?.closest('input, textarea, select, [contenteditable], [role="tab"]')) return;
    cycleDictionary(e.key === 'ArrowRight' ? 1 : -1);
  }

  onMounted(() => {
    if (props.arrowKeyNav) window.addEventListener('keydown', onWindowKeydown);
  });
  onUnmounted(() => window.removeEventListener('keydown', onWindowKeydown));

  defineExpose({ cycleDictionary });
</script>

<template>
  <!-- Single JMDict group: render identically to current behaviour -->
  <template v-if="singleJmDictOnly && resolvedGroups[0].jmDictDefinitions">
    <VocabularyDefinitions
      :definitions="resolvedGroups[0].jmDictDefinitions"
      :is-compact="isCompact"
      :max-definitions="maxDefinitions"
      :current-reading-index="currentReadingIndex"
      :readings="readings"
      :word-id="wordId"
      :hidden-behaviour="hiddenBehaviour"
    />
  </template>

  <!-- Multiple groups: tabbed view -->
  <template v-else-if="!isCompact && hasMultipleGroups">
    <!-- select-on-focus: after clicking a tab, focus stays on the tab header; arrows there are
         handled by PrimeVue's own (wrapping) tab navigation, which the window listener skips. -->
    <Tabs v-model:value="activeTab" :show-navigators="false" select-on-focus @update:value="onTabSelected">
      <TabList class="dict-tabs">
        <Tab v-for="group in resolvedGroups" :key="group.dictionaryId" :value="group.dictionaryId">
          {{ group.dictionaryName }}
        </Tab>
        <DictionaryFontSizeControl v-if="fontControls && activeGroupIsCustom" class="ml-1 self-center" />
      </TabList>
      <TabPanels>
        <TabPanel v-for="group in resolvedGroups" :key="group.dictionaryId" :value="group.dictionaryId">
          <div>
            <VocabularyDefinitions
              v-if="group.isJmDict && group.jmDictDefinitions"
              :definitions="group.jmDictDefinitions"
              :is-compact="false"
              :max-definitions="maxDefinitions"
              :current-reading-index="currentReadingIndex"
              :readings="readings"
              :word-id="wordId"
              :hidden-behaviour="hiddenBehaviour"
            />
            <div v-else-if="group.customDefinitions" class="custom-dict-content" :style="customFontStyle" v-html="definitionsToHtml(group.customDefinitions)" />
          </div>
        </TabPanel>
      </TabPanels>
    </Tabs>
  </template>

  <!-- Single custom group expanded -->
  <template v-else-if="!isCompact">
    <div v-if="resolvedGroups.length > 0 && resolvedGroups[0].customDefinitions">
      <DictionaryFontSizeControl v-if="fontControls" class="mb-1" />
      <div class="custom-dict-content" :style="customFontStyle" v-html="definitionsToHtml(resolvedGroups[0].customDefinitions)" />
    </div>
  </template>

  <!-- Compact mode: show first group only -->
  <template v-else>
    <div v-if="resolvedGroups.length > 0">
      <template v-if="resolvedGroups[0].isJmDict && resolvedGroups[0].jmDictDefinitions">
        <VocabularyDefinitions
          :definitions="resolvedGroups[0].jmDictDefinitions"
          :is-compact="true"
          :current-reading-index="currentReadingIndex"
          :readings="readings"
          :word-id="wordId"
          :hidden-behaviour="hiddenBehaviour"
        />
      </template>
      <template v-else-if="resolvedGroups[0].customDefinitions && !store.hideVocabularyDefinitions">
        <span class="custom-dict-compact" :style="customFontStyle" v-html="definitionsToHtml(resolvedGroups[0].customDefinitions)" />
      </template>
      <span v-if="visibleGroupCount > 1" class="text-xs text-gray-400 dark:text-gray-400 ml-1">
        +{{ visibleGroupCount - 1 }} more {{ visibleGroupCount - 1 === 1 ? 'dictionary' : 'dictionaries' }}
      </span>
    </div>
  </template>
</template>

<style scoped>
  .dict-tabs :deep([data-pc-name='tab']) {
    padding: 0.35rem 0.75rem;
    font-size: 0.75rem;
  }

  .dict-tabs {
    padding: 0;
  }

  :deep([data-pc-name='tabpanels']) {
    padding: 0;
  }

  :deep([data-pc-name='tabpanel']) {
    padding: 0;
  }

  :deep([data-pc-name='tabs']) {
    padding: 0;
  }

  .custom-dict-compact {
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .custom-dict-compact :deep(*) {
    display: inline;
    margin: 0;
    padding: 0;
  }

  .custom-dict-compact :deep(br) {
    content: ' ';
  }

  .custom-dict-compact :deep(li + li)::before {
    content: '; ';
  }

  .custom-dict-compact :deep(ol),
  .custom-dict-compact :deep(ul) {
    list-style: none;
  }
</style>
