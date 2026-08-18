<script setup lang="ts">
  import { ref, computed, watch, onMounted, onBeforeUnmount, nextTick } from 'vue';
  import Tag from 'primevue/tag';
  import { Genre, type TagWithPercentage } from '~/types';
  import { getGenreText } from '~/utils/genreMapper';

  interface Props {
    genres?: Genre[];
    tags?: TagWithPercentage[];
    label: string;
    genreColourClasses?: string;
    tagColourClasses?: string;
    minVisibleItems?: number;
    buttonBuffer?: number;
    gapSize?: number;
  }

  const props = withDefaults(defineProps<Props>(), {
    minVisibleItems: 1,
    buttonBuffer: 75,
    gapSize: 6,
  });

  const expanded = ref(false);
  const containerRef = ref<HTMLElement | null>(null);
  const labelRef = ref<HTMLElement | null>(null);
  const itemRefs = ref<HTMLElement[]>([]);
  const visibleCount = ref<number>(Infinity);
  const isCalculating = ref(false);

  const items = computed(() => {
    if (props.genres) {
      return props.genres.map((genre, index) => ({
        id: `genre-${genre}-${index}`,
        type: 'genre' as const,
        data: genre,
      }));
    }
    if (props.tags) {
      return props.tags.map((tag, index) => ({
        id: `tag-${tag.tagId}-${index}`,
        type: 'tag' as const,
        data: tag,
      }));
    }
    return [];
  });

  const hasOverflow = computed(() => !expanded.value && items.value.length > visibleCount.value);
  const hiddenCount = computed(() => Math.max(0, items.value.length - visibleCount.value));

  const calculateVisibleCount = async () => {
    if (!containerRef.value || items.value.length === 0 || expanded.value || isCalculating.value) {
      return;
    }

    isCalculating.value = true;

    // 1. Reset to show all items so we can measure their true widths
    visibleCount.value = items.value.length;
    await nextTick();

    if (!containerRef.value || !labelRef.value) {
      isCalculating.value = false;
      return;
    }

    const containerWidth = containerRef.value.clientWidth;

    // 2. Start accumulated width with the Label's width + the gap after it
    // We use mr-1 (4px) in template, plus gap-1.5 (6px) from flex parent
    const labelWidth = labelRef.value.offsetWidth;
    const labelMargin = 4; // approximate for mr-1

    // Initial used space: Label + Margin + The Flex Gap that will appear before the first item
    let accumulatedWidth = labelWidth + labelMargin;

    let count = 0;

    for (let i = 0; i < itemRefs.value.length; i++) {
      const el = itemRefs.value[i];
      if (!el) continue;

      const itemWidth = el.offsetWidth;

      // Add gap before this item (if it's not the very first thing in the container, which the label is)
      accumulatedWidth += props.gapSize;

      const currentTotalWidth = accumulatedWidth + itemWidth;

      // Check if adding this item + the "More" button buffer exceeds container
      if (currentTotalWidth + props.buttonBuffer <= containerWidth) {
        accumulatedWidth += itemWidth;
        count++;
      } else {
        // If we can't fit this item, stop here
        break;
      }
    }

    // Ensure we show at least the minimum, unless total items is less
    visibleCount.value = Math.max(props.minVisibleItems, Math.min(count, items.value.length));

    isCalculating.value = false;
  };

  let resizeObserver: ResizeObserver | null = null;

  onMounted(() => {
    // Initial calculation
    calculateVisibleCount();

    if (containerRef.value) {
      resizeObserver = new ResizeObserver(() => {
        // Debounce slightly to avoid heavy calculations on every pixel drag
        window.requestAnimationFrame(() => {
          calculateVisibleCount();
        });
      });
      resizeObserver.observe(containerRef.value);
    }
  });

  onBeforeUnmount(() => {
    if (resizeObserver && containerRef.value) {
      resizeObserver.disconnect();
    }
  });

  watch(
    () => items.value,
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
      calculateVisibleCount();
    } else {
      visibleCount.value = items.value.length;
    }
  };
</script>

<template>
  <div v-if="items.length > 0" ref="containerRef" class="flex flex-wrap gap-1.5 items-center w-full relative">
    <span ref="labelRef" class="text-xs font-semibold text-gray-600 dark:text-gray-400 uppercase tracking-wider mr-1 shrink-0">
      {{ label }}
    </span>

    <!-- Genres -->
    <template v-if="items[0]?.type === 'genre'">
      <div
        v-for="(item, index) in items"
        :key="item.id"
        :ref="
          (el) => {
            if (el) itemRefs[index] = el as HTMLElement;
          }
        "
        :class="{ hidden: !expanded && index >= visibleCount }"
      >
        <Tag
          :value="getGenreText(item.data as Genre)"
          :class="[genreColourClasses ?? '!bg-purple-100 dark:!bg-purple-900/50 !text-purple-700 dark:!text-purple-200', '!font-normal text-xs !py-0.5 !px-2']"
          rounded
        />
      </div>
    </template>

    <!-- Tags -->
    <template v-else-if="items[0]?.type === 'tag'">
      <div
        v-for="(item, index) in items"
        :key="item.id"
        :ref="
          (el) => {
            if (el) itemRefs[index] = el as HTMLElement;
          }
        "
        :class="{ hidden: !expanded && index >= visibleCount }"
      >
        <Tag
          :class="[
            tagColourClasses ?? '!bg-gray-100 dark:!bg-gray-800 !text-gray-600 dark:text-gray-400 dark:!text-gray-300',
            '!font-normal transition-all text-xs !py-0.5 !px-2',
          ]"
          :style="{
            opacity: 0.8 + ((item.data as TagWithPercentage).percentage / 100) * 0.2,
          }"
          rounded
        >
          {{ (item.data as TagWithPercentage).name }}
          <!--          <span class="ml-1 opacity-70 scale-90">{{ (item.data as TagWithPercentage).percentage }}%</span>-->
        </Tag>
      </div>
    </template>

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
  </div>
</template>
