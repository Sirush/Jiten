<script setup lang="ts">
  import { ref, computed, watch, onMounted, onBeforeUnmount, nextTick, type ComponentPublicInstance } from 'vue';
  import Tag from 'primevue/tag';
  import { type DeckRelationship, DeckRelationshipType, MediaGroupKind, type SeriesRef, SeriesKind } from '~/types';
  import { franchisePath } from '~/utils/mediaGroup';
  import { relatedMediaLabel } from '~/utils/relationshipRoles';

  interface Props {
    relationships: DeckRelationship[];
    series?: SeriesRef[];
    franchiseId?: number | null;
    /** Passed as ?deck= so the franchise page marks this deck. */
    deckId?: number;
    minVisibleItems?: number;
    buttonBuffer?: number;
    gapSize?: number;
  }

  const props = withDefaults(defineProps<Props>(), {
    series: () => [],
    minVisibleItems: 1,
    buttonBuffer: 80,
    gapSize: 16,
  });

  const localiseTitle = useLocaliseTitle();

  const relationshipSortOrder: DeckRelationshipType[] = [
    DeckRelationshipType.Sequel,
    DeckRelationshipType.Prequel,
    DeckRelationshipType.Adaptation,
    DeckRelationshipType.Alternative,
    DeckRelationshipType.Fandisc,
    DeckRelationshipType.Spinoff,
    DeckRelationshipType.SideStory,
    DeckRelationshipType.HasFandisc,
    DeckRelationshipType.HasSpinoff,
    DeckRelationshipType.HasSideStory,
    DeckRelationshipType.SourceMaterial,
    DeckRelationshipType.SameSeries,
    DeckRelationshipType.SameSetting,
  ];

  const expanded = ref(false);
  const containerRef = ref<HTMLElement | null>(null);
  const labelRef = ref<HTMLElement | null>(null);
  const franchiseRef = ref<ComponentPublicInstance | HTMLElement | null>(null);
  const itemRefs = ref<HTMLElement[]>([]);
  const visibleCount = ref<number>(20); // Default to a high number initially
  const isCalculating = ref(false);

  interface RelatedItem {
    key: string;
    label: string;
    text: string;
    to: string;
  }

  // Settings span franchises and have no page of their own.
  const memberSeries = computed(() => props.series.filter((s) => s.kind === SeriesKind.Series));
  const primarySeries = computed(() => memberSeries.value[0] ?? null);

  const groupLink = computed(() => {
    if (props.franchiseId == null) return null;
    const series = primarySeries.value;
    const path = franchisePath(props.franchiseId, series ? { kind: MediaGroupKind.Series, id: series.seriesId } : null);
    const to = props.deckId != null ? `${path}${path.includes('?') ? '&' : '?'}deck=${props.deckId}` : path;
    return { to, text: series ? 'View series' : 'View franchise' };
  });

  const seriesItems = computed<RelatedItem[]>(() =>
    props.franchiseId == null
      ? []
      : memberSeries.value
          .filter((s) => s !== primarySeries.value)
          .map((s) => ({
            key: `series-${s.seriesId}`,
            label: 'Series',
            text: localiseTitle(s),
            to: franchisePath(props.franchiseId!, { kind: MediaGroupKind.Series, id: s.seriesId }),
          }))
  );

  const relationshipItems = computed<RelatedItem[]>(() =>
    [...props.relationships]
      .sort((a, b) => {
        const indexA = relationshipSortOrder.indexOf(a.relationshipType);
        const indexB = relationshipSortOrder.indexOf(b.relationshipType);
        return (indexA === -1 ? 99 : indexA) - (indexB === -1 ? 99 : indexB);
      })
      .map((rel) => ({
        key: `${rel.targetDeckId}-${rel.relationshipType}`,
        label: relatedMediaLabel(rel.relationshipType),
        text: localiseTitle(rel.targetDeck),
        to: `/decks/media/${rel.targetDeckId}/detail`,
      }))
  );

  const sortedRelationships = computed(() => [...seriesItems.value, ...relationshipItems.value]);

  const hasOverflow = computed(() => sortedRelationships.value.length > visibleCount.value);
  const hiddenCount = computed(() => Math.max(0, sortedRelationships.value.length - visibleCount.value));

  const calculateVisibleCount = async () => {
    if (!containerRef.value || sortedRelationships.value.length === 0 || expanded.value) return;

    isCalculating.value = true;

    // 1. Temporarily show all items to measure them accurately
    const prevVisibleCount = visibleCount.value;
    visibleCount.value = sortedRelationships.value.length;

    // 2. Wait for DOM to render all items
    await nextTick();

    if (!containerRef.value || !labelRef.value) {
      isCalculating.value = false;
      return;
    }

    const containerWidth = containerRef.value.getBoundingClientRect().width;
    const labelWidth = labelRef.value.getBoundingClientRect().width;

    // The group link is always visible, so its width is reserved up front like the label.
    const fr = franchiseRef.value;
    const franchiseEl: unknown = fr instanceof HTMLElement ? fr : fr?.$el;
    const franchiseWidth = franchiseEl instanceof Element ? franchiseEl.getBoundingClientRect().width : 0;

    let accumulatedWidth = labelWidth + 4 + (franchiseWidth > 0 ? franchiseWidth + props.gapSize : 0); // Label + margin
    let count = 0;

    for (let i = 0; i < itemRefs.value.length; i++) {
      const el = itemRefs.value[i];
      if (!el) continue;

      // Get width of the actual DOM element
      const itemWidth = el.getBoundingClientRect().width;
      const currentGap = i === 0 ? 0 : props.gapSize;

      // logic: If we add this item, will we need the "+X more" button?
      const isLastItem = i === sortedRelationships.value.length - 1;
      const neededBuffer = isLastItem ? 0 : props.buttonBuffer;

      if (accumulatedWidth + currentGap + itemWidth + neededBuffer <= containerWidth) {
        accumulatedWidth += currentGap + itemWidth;
        count++;
      } else {
        break;
      }
    }

    visibleCount.value = Math.max(props.minVisibleItems, count);
    isCalculating.value = false;
  };

  let resizeObserver: ResizeObserver | null = null;

  onMounted(() => {
    calculateVisibleCount();
    if (containerRef.value) {
      resizeObserver = new ResizeObserver(() => {
        if (!expanded.value) calculateVisibleCount();
      });
      resizeObserver.observe(containerRef.value);
    }
  });

  onBeforeUnmount(() => {
    resizeObserver?.disconnect();
  });

  watch(
    () => [props.relationships, props.series, props.franchiseId],
    () => {
      expanded.value = false;
      itemRefs.value = [];
      nextTick(calculateVisibleCount);
    },
    { deep: true }
  );

  const toggleExpanded = () => {
    expanded.value = !expanded.value;
    if (!expanded.value) {
      nextTick(calculateVisibleCount);
    }
  };
</script>

<template>
  <div v-if="sortedRelationships.length > 0 || groupLink" ref="containerRef" class="flex flex-wrap gap-x-4 gap-y-1 items-center w-full relative">
    <span ref="labelRef" class="text-xs font-semibold text-gray-600 dark:text-gray-400 uppercase tracking-wider mr-1 shrink-0">Related</span>

    <NuxtLink
      v-for="(item, index) in sortedRelationships"
      v-show="expanded || index < visibleCount || isCalculating"
      :key="item.key"
      :ref="
        (el: any) => {
          if (el) itemRefs[index] = el.$el || el;
        }
      "
      :to="item.to"
      class="text-xs whitespace-nowrap no-underline hover:underline underline-offset-2 transition-colors"
    >
      <span class="text-gray-600 dark:text-gray-400">{{ item.label }}:</span>
      <span class="ml-1 text-primary" v-bind="japaneseTextAttrs(item.text)">{{ item.text }}</span>
    </NuxtLink>

    <Tag
      v-if="hasOverflow || expanded"
      class="cursor-pointer hover:!bg-gray-200 dark:hover:!bg-gray-700 transition-colors text-xs !py-0.5 !px-2"
      severity="secondary"
      rounded
      @click="toggleExpanded"
    >
      <span class="flex items-center gap-1">
        {{ expanded ? 'Less' : `+${hiddenCount}` }}
        <i :class="['pi text-[10px]', expanded ? 'pi-chevron-up' : 'pi-chevron-down']" />
      </span>
    </Tag>

    <NuxtLink
      v-if="groupLink"
      ref="franchiseRef"
      :to="groupLink.to"
      class="text-xs whitespace-nowrap text-primary font-medium no-underline hover:underline underline-offset-2 transition-colors"
    >
      {{ groupLink.text }} →
    </NuxtLink>
  </div>
</template>
