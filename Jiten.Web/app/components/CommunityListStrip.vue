<script setup lang="ts">
  import { useEventListener, useResizeObserver } from '@vueuse/core';
  import type { CommunityListSummary } from '~/types/types';

  const props = defineProps<{ lists: CommunityListSummary[]; loading: boolean }>();

  const EDGE_TOLERANCE_PX = 4;

  const scroller = ref<HTMLElement | null>(null);
  const canScrollBack = ref(false);
  const canScrollForward = ref(false);

  function updateEdges() {
    const el = scroller.value;
    if (!el) return;
    canScrollBack.value = el.scrollLeft > EDGE_TOLERANCE_PX;
    canScrollForward.value = el.scrollLeft + el.clientWidth < el.scrollWidth - EDGE_TOLERANCE_PX;
  }

  function scrollByPage(direction: 1 | -1) {
    const el = scroller.value;
    const card = el?.firstElementChild as HTMLElement | null;
    if (!el || !card) return;
    const step = card.offsetWidth + parseFloat(getComputedStyle(el).columnGap || '0');
    const cardsPerPage = Math.max(1, Math.floor(el.clientWidth / step));
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    el.scrollBy({ left: direction * step * cardsPerPage, behavior: reducedMotion ? 'auto' : 'smooth' });
  }

  useEventListener(scroller, 'scroll', updateEdges, { passive: true });
  useResizeObserver(scroller, updateEdges);
  watch(
    () => [props.lists, props.loading],
    () => nextTick(updateEdges),
    { flush: 'post' }
  );

  /** Arrows stay out of the tab order and the accessibility tree: every card is a link that Tab scrolls into view. */
  const arrowClass =
    'absolute inset-y-0 z-10 hidden w-10 cursor-pointer items-center justify-center bg-surface-0/75 text-surface-900 opacity-0 transition-[opacity,color] group-hover/strip:opacity-100 hover:text-primary-700 dark:bg-surface-950/70 dark:text-surface-0 dark:hover:text-primary-300 [@media(hover:hover)]:flex';
</script>

<template>
  <div class="group/strip relative">
    <div v-if="loading" class="-m-1 flex gap-3 overflow-hidden p-1">
      <CommunityListCardSkeleton v-for="n in 3" :key="n" compact />
    </div>
    <template v-else>
      <div
        ref="scroller"
        class="no-scrollbar -m-1 flex snap-x snap-mandatory scroll-px-1 gap-3 overflow-x-auto p-1 [@media(hover:hover)]:scroll-pl-13"
      >
        <CommunityListCard v-for="list in lists" :key="list.listId" :list="list" compact />
      </div>
      <button v-show="canScrollBack" type="button" tabindex="-1" aria-hidden="true" :class="[arrowClass, '-left-1']" @click="scrollByPage(-1)">
        <i class="pi pi-chevron-left text-xl" />
      </button>
      <button v-show="canScrollForward" type="button" tabindex="-1" aria-hidden="true" :class="[arrowClass, '-right-1']" @click="scrollByPage(1)">
        <i class="pi pi-chevron-right text-xl" />
      </button>
    </template>
  </div>
</template>
