<script setup lang="ts">
import { ref, computed, watch, onMounted, onBeforeUnmount, nextTick, type ComponentPublicInstance } from 'vue';
import Tag from 'primevue/tag';
import { type DeckRelationship, DeckRelationshipType } from '~/types';

interface Props {
  relationships: DeckRelationship[];
  /** When set, appends an always-visible "View franchise" tag linking to the franchise page. */
  deckId?: number;
  minVisibleItems?: number;
  buttonBuffer?: number;
  gapSize?: number;
}

const props = withDefaults(defineProps<Props>(), {
  minVisibleItems: 1,
  buttonBuffer: 80,
  gapSize: 6,
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
];

const relationshipTypeLabels: Record<DeckRelationshipType, string> = {
  [DeckRelationshipType.Sequel]: 'Prequel',
  [DeckRelationshipType.Fandisc]: 'Source',
  [DeckRelationshipType.Spinoff]: 'Source',
  [DeckRelationshipType.SideStory]: 'Source',
  [DeckRelationshipType.Adaptation]: 'Adaptation',
  [DeckRelationshipType.Alternative]: 'Alternative',
  [DeckRelationshipType.Prequel]: 'Sequel',
  [DeckRelationshipType.HasFandisc]: 'Fandisc',
  [DeckRelationshipType.HasSpinoff]: 'Spinoff',
  [DeckRelationshipType.HasSideStory]: 'Side Story',
  [DeckRelationshipType.SourceMaterial]: 'Source',
};

function getRelationshipTypeLabel(type: DeckRelationshipType): string {
  return relationshipTypeLabels[type] ?? 'Unknown';
}

const expanded = ref(false);
const containerRef = ref<HTMLElement | null>(null);
const labelRef = ref<HTMLElement | null>(null);
const franchiseRef = ref<ComponentPublicInstance | HTMLElement | null>(null);
const itemRefs = ref<HTMLElement[]>([]);
const visibleCount = ref<number>(20); // Default to a high number initially
const isCalculating = ref(false);

const sortedRelationships = computed(() => {
  return [...props.relationships].sort((a, b) => {
    const indexA = relationshipSortOrder.indexOf(a.relationshipType);
    const indexB = relationshipSortOrder.indexOf(b.relationshipType);
    return (indexA === -1 ? 99 : indexA) - (indexB === -1 ? 99 : indexB);
  });
});

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

  // The "View franchise" tag is always visible, so reserve its width up front like the label.
  const fr = franchiseRef.value;
  const franchiseEl = fr instanceof HTMLElement ? fr : (fr?.$el as HTMLElement | undefined);
  const franchiseWidth = franchiseEl?.getBoundingClientRect().width ?? 0;

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
      accumulatedWidth += (currentGap + itemWidth);
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

watch(() => props.relationships, () => {
  expanded.value = false;
  itemRefs.value = [];
  nextTick(calculateVisibleCount);
}, { deep: true });

const toggleExpanded = () => {
  expanded.value = !expanded.value;
  if (!expanded.value) {
    nextTick(calculateVisibleCount);
  }
};
</script>

<template>
  <div
    v-if="sortedRelationships.length > 0"
    ref="containerRef"
    class="flex flex-wrap gap-1.5 items-center w-full relative"
  >
    <span ref="labelRef" class="text-xs font-semibold text-gray-500 uppercase tracking-wider mr-1 shrink-0">
      Related
    </span>

    <NuxtLink
      v-for="(rel, index) in sortedRelationships"
      :key="`${rel.targetDeckId}-${rel.relationshipType}`"
      :ref="(el: any) => { if (el) itemRefs[index] = el.$el || el }"
      :to="`/decks/media/${rel.targetDeckId}/detail`"
      v-show="expanded || index < visibleCount || isCalculating"
      class="px-2 py-0.5 bg-surface-100 dark:bg-surface-900/50 text-surface-700 dark:text-surface-200 rounded-full text-xs hover:bg-surface-200 dark:hover:bg-surface-800/50 transition-colors whitespace-nowrap"
    >
      <span class="font-medium">{{ getRelationshipTypeLabel(rel.relationshipType) }}:</span>
      <span class="ml-1">{{ localiseTitle(rel.targetDeck) }}</span>
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
        <i :class="['pi text-[10px]', expanded ? 'pi-chevron-up' : 'pi-chevron-down']"></i>
      </span>
    </Tag>

    <NuxtLink
      v-if="deckId != null"
      ref="franchiseRef"
      :to="`/decks/media/${deckId}/franchise`"
      class="px-2 py-0.5 rounded-full text-xs whitespace-nowrap border border-primary text-primary font-medium hover:bg-primary/10 transition-colors"
    >
      View franchise →
    </NuxtLink>
  </div>
</template>
