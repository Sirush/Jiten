<script setup lang="ts">
  import Tabs from 'primevue/tabs';
  import TabList from 'primevue/tablist';
  import Tab from 'primevue/tab';
  import { type Franchise, type FranchiseViewKind, type MediaGroupRef, type MediaType, MediaGroupKind } from '~/types';
  import { franchiseDisplayName, franchiseHasSideLinks, franchiseUsableEdges, resolveFranchiseView } from '~/utils/franchiseLayout';
  import { parseNumberArray } from '~/utils/queryParams';
  import {
    activeScopeChip,
    buildScopeChips,
    filterNodesByMediaTypes,
    filterNodesByScope,
    formatScope,
    parseScope,
    scopeDeckIds,
    type ScopeChip,
  } from '~/utils/mediaGroup';

  type PageView = FranchiseViewKind | 'vocabulary';

  const props = defineProps<{
    franchise: Franchise & { franchiseId: number };
    currentDeckId?: number | null;
  }>();

  const emit = defineEmits<{ renamed: [] }>();

  const route = useRoute();
  const router = useRouter();
  const localiseTitle = useLocaliseTitle();

  const name = computed(() => franchiseDisplayName(props.franchise, localiseTitle) || 'Untitled');

  const requestedScope = computed(() => parseScope(route.query.scope));
  const scopeIds = computed(() => scopeDeckIds(props.franchise, requestedScope.value));
  const chips = computed(() => buildScopeChips(props.franchise, localiseTitle));
  const activeChip = computed(() => activeScopeChip(chips.value, scopeIds.value ? requestedScope.value : null, scopeIds.value));
  const scope = computed(() => (activeChip.value ? activeChip.value.scope : scopeIds.value ? requestedScope.value : null));
  const group = computed<MediaGroupRef>(() => scope.value ?? { kind: MediaGroupKind.Franchise, id: props.franchise.franchiseId });
  const groupName = computed(() => (activeChip.value?.scope ? activeChip.value.label : name.value));

  function selectScope(chip: ScopeChip) {
    if (chip.key === activeChip.value?.key) return;
    router.push({ query: { ...route.query, scope: formatScope(chip.scope), offset: undefined } });
  }

  const scopedNodes = computed(() => filterNodesByScope(props.franchise.nodes, scopeIds.value));

  const mediaTypes = computed<MediaType[]>({
    get: () => {
      const present = new Set(scopedNodes.value.map((n) => n.mediaType));
      return (parseNumberArray(route.query.mediaTypes) as MediaType[]).filter((t) => present.has(t));
    },
    set: (value) => {
      router.replace({ query: { ...route.query, mediaTypes: value.length > 0 ? value.join(',') : undefined, offset: undefined } });
    },
  });

  const selectedNodes = computed(() => filterNodesByMediaTypes(scopedNodes.value, mediaTypes.value));
  const highlightDeckIds = computed(() => (scopeIds.value || mediaTypes.value.length > 0 ? selectedNodes.value.map((n) => n.deckId) : null));

  const { stats, status: statsStatus, reload: reloadStats } = useMediaGroupStats(group, mediaTypes);

  const viewOptions: { label: string; value: PageView }[] = [
    { label: 'Timeline', value: 'timeline' },
    { label: 'Series', value: 'series' },
    { label: 'Web', value: 'web' },
    { label: 'Vocabulary', value: 'vocabulary' },
  ];
  const view = computed<PageView>(() => {
    const query = Array.isArray(route.query.view) ? route.query.view[0] : route.query.view;
    return query === 'vocabulary' ? 'vocabulary' : resolveFranchiseView(query, props.franchise);
  });

  // router.replace keeps toggling out of history; the default view keeps a clean URL.
  function selectView(value: string | number) {
    const option = viewOptions.find((o) => o.value === value);
    if (!option) return;
    const isDefault = option.value === resolveFranchiseView(undefined, props.franchise);
    router.replace({ query: { ...route.query, view: isDefault ? undefined : option.value, offset: undefined } });
  }

  const excludedDeckIds = computed(() => {
    const present = new Set(scopedNodes.value.map((n) => n.deckId));
    return parseNumberArray(route.query.excludeDeckIds).filter((id) => present.has(id));
  });

  const vocabularyKey = computed(() => `${group.value.kind}:${group.value.id}`);
  const hasSideLinks = computed(() => franchiseHasSideLinks(franchiseUsableEdges(props.franchise.nodes, props.franchise.edges)));
</script>

<template>
  <div class="flex flex-col gap-4">
    <FranchiseHeader
      v-model:media-types="mediaTypes"
      :franchise="franchise"
      :name="name"
      :chips="chips"
      :active-chip="activeChip"
      :scoped-nodes="scopedNodes"
      :selected-nodes="selectedNodes"
      :stats="stats"
      :stats-status="statsStatus"
      :current-deck-id="currentDeckId"
      @select-scope="selectScope"
      @renamed="emit('renamed')"
      @retry-stats="reloadStats"
    />

    <div class="flex flex-col gap-4">
      <div class="flex flex-wrap items-end gap-y-2 md:flex-nowrap md:border-b md:border-(--p-tabs-tablist-border-color)">
        <Tabs :value="view" :show-navigators="false" scrollable class="w-full min-w-0 md:-mb-px md:w-auto md:flex-1" @update:value="selectView">
          <TabList :pt="{ root: { class: 'bg-transparent!' }, tabList: { class: 'bg-transparent! md:border-transparent!' } }">
            <Tab v-for="option in viewOptions" :key="option.value" :value="option.value" class="min-h-11 px-2.5! py-2.5! text-sm sm:px-4!">
              {{ option.label }}
            </Tab>
          </TabList>
        </Tabs>
        <div class="flex w-full shrink-0 items-center justify-end gap-2 md:w-auto md:pl-2">
          <FranchiseDisplayOptions v-if="view === 'timeline' || view === 'series'" :view="view" :show-links="hasSideLinks" />
          <FranchiseStudyButton :group="group" :group-name="groupName" :members="scopedNodes" :media-types="mediaTypes" :excluded-deck-ids="excludedDeckIds" />
        </div>
      </div>

      <FranchiseVocabulary
        v-if="view === 'vocabulary'"
        :key="vocabularyKey"
        :group="group"
        :label="groupName"
        :members="selectedNodes"
        :media-types="mediaTypes"
        :unique-word-count="statsStatus === 'ready' ? (stats?.uniqueWordCount ?? null) : null"
      />
      <FranchiseWeb v-else-if="view === 'web'" :franchise="franchise" :current-deck-id="currentDeckId" :scope-deck-ids="highlightDeckIds" />
      <FranchiseSeriesView v-else-if="view === 'series'" :franchise="franchise" :current-deck-id="currentDeckId" :scope-deck-ids="highlightDeckIds" />
      <FranchiseTimeline v-else :franchise="franchise" :current-deck-id="currentDeckId" :scope-deck-ids="highlightDeckIds" />
    </div>
  </div>
</template>
