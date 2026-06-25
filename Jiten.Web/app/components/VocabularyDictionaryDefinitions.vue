<script setup lang="ts">
  import type { Reading } from '~/types';
  import type { ResolvedDefinitionGroup } from '~/composables/useYomitanDictionary';
  import { definitionsToHtml } from '~/composables/useYomitanDictionary';

  const props = defineProps<{
    resolvedGroups: readonly ResolvedDefinitionGroup[];
    isCompact: boolean;
    currentReadingIndex?: number;
    readings?: Reading[];
  }>();

  const store = useJitenStore();

  const singleJmDictOnly = computed(() =>
    props.resolvedGroups.length === 1 && props.resolvedGroups[0].isJmDict,
  );

  const hasMultipleGroups = computed(() => props.resolvedGroups.length > 1);
  const visibleGroupCount = computed(() => props.resolvedGroups.length);
  const activeTab = ref<string | undefined>(undefined);

  watch(() => props.resolvedGroups, (groups) => {
    if (groups.length > 0 && !activeTab.value) {
      activeTab.value = groups[0].dictionaryId;
    }
  }, { immediate: true });
</script>

<template>
  <!-- Single JMDict group: render identically to current behaviour -->
  <template v-if="singleJmDictOnly && resolvedGroups[0].jmDictDefinitions">
    <VocabularyDefinitions
      :definitions="resolvedGroups[0].jmDictDefinitions"
      :is-compact="isCompact"
      :current-reading-index="currentReadingIndex"
      :readings="readings"
    />
  </template>

  <!-- Multiple groups: tabbed view -->
  <template v-else-if="!isCompact && hasMultipleGroups">
    <Tabs v-model:value="activeTab" :show-navigators="false">
      <TabList class="dict-tabs">
        <Tab v-for="group in resolvedGroups" :key="group.dictionaryId" :value="group.dictionaryId">
          {{ group.dictionaryName }}
        </Tab>
      </TabList>
      <TabPanels>
        <TabPanel v-for="group in resolvedGroups" :key="group.dictionaryId" :value="group.dictionaryId">
          <div>
            <VocabularyDefinitions
              v-if="group.isJmDict && group.jmDictDefinitions"
              :definitions="group.jmDictDefinitions"
              :is-compact="false"
              :current-reading-index="currentReadingIndex"
              :readings="readings"
            />
            <div
              v-else-if="group.customDefinitions"
              class="custom-dict-content text-sm"
              v-html="definitionsToHtml(group.customDefinitions)"
            />
          </div>
        </TabPanel>
      </TabPanels>
    </Tabs>
  </template>

  <!-- Single custom group expanded -->
  <template v-else-if="!isCompact">
    <div v-if="resolvedGroups.length > 0 && resolvedGroups[0].customDefinitions">
      <div
        class="custom-dict-content text-sm"
        v-html="definitionsToHtml(resolvedGroups[0].customDefinitions)"
      />
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
        />
      </template>
      <template v-else-if="resolvedGroups[0].customDefinitions && !store.hideVocabularyDefinitions">
        <span class="custom-dict-compact text-sm" v-html="definitionsToHtml(resolvedGroups[0].customDefinitions)" />
      </template>
      <span v-if="visibleGroupCount > 1" class="text-xs text-gray-400 dark:text-gray-500 ml-1">
        +{{ visibleGroupCount - 1 }} more {{ visibleGroupCount - 1 === 1 ? 'dictionary' : 'dictionaries' }}
      </span>
    </div>
  </template>
</template>

<style scoped>
.dict-tabs :deep([data-pc-name="tab"]) {
  padding: 0.35rem 0.75rem;
  font-size: 0.75rem;
}

.dict-tabs {
  padding: 0;
}

:deep([data-pc-name="tabpanels"]) {
  padding: 0;
}

:deep([data-pc-name="tabpanel"]) {
  padding: 0;
}

:deep([data-pc-name="tabs"]) {
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
