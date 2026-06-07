<!--
  Lightweight tooltip wrapper. On mount it only renders the slot and attaches
  hover/tap listeners — the expensive floating-ui positioning + teleported popup
  (TooltipPopup.vue) is mounted lazily on first interaction. This keeps pages
  that render many tooltips (e.g. deck lists with ~15 per card) cheap to mount.
-->
<template>
  <div ref="wrapperRef" :style="block ? undefined : { display: 'contents' }">
    <slot :toggle="toggle" :show="show" :hide="hide" />
  </div>

  <TooltipPopup
    v-if="activated"
    :content="content"
    :reference-el="referenceEl"
    :placement="placement"
    :offset="offset"
    :visible="isVisible"
    :is-mobile="isMobile"
    @request-hide="hide"
  />
</template>

<script setup lang="ts">
  import { ref, computed, onMounted, onBeforeUnmount } from 'vue';

  interface Props {
    content: string;
    placement?: 'top' | 'bottom' | 'left' | 'right';
    offset?: number;
    block?: boolean;
  }

  const props = withDefaults(defineProps<Props>(), {
    placement: 'top',
    offset: 8,
  });

  const wrapperRef = ref<HTMLElement | null>(null);
  const referenceEl = computed<HTMLElement | null>(() =>
    props.block ? wrapperRef.value : wrapperRef.value?.firstElementChild as HTMLElement | null
  );
  const isVisible = ref(false);
  const isMobile = ref(false);
  // Becomes true on the first interaction, mounting TooltipPopup (and its
  // floating-ui setup) only then. Never reset, so a re-shown tooltip is instant.
  const activated = ref(false);

  const show = () => {
    activated.value = true;
    isVisible.value = true;
  };

  const hide = () => {
    isVisible.value = false;
  };

  const toggle = () => {
    if (isVisible.value) hide();
    else show();
  };

  const handleMouseEnter = () => {
    if (!isMobile.value) show();
  };

  const handleMouseLeave = () => {
    if (!isMobile.value) hide();
  };

  const handleClick = (e: Event) => {
    if (isMobile.value) {
      e.preventDefault();
      e.stopPropagation();
      toggle();
    }
  };

  const checkMobile = () => {
    isMobile.value = 'ontouchstart' in window || navigator.maxTouchPoints > 0;
  };

  onMounted(() => {
    checkMobile();

    const el = referenceEl.value;
    if (el) {
      el.addEventListener('mouseenter', handleMouseEnter, { passive: true });
      el.addEventListener('mouseleave', handleMouseLeave, { passive: true });
      el.addEventListener('click', handleClick);
    }
  });

  onBeforeUnmount(() => {
    const el = referenceEl.value;
    if (el) {
      el.removeEventListener('mouseenter', handleMouseEnter);
      el.removeEventListener('mouseleave', handleMouseLeave);
      el.removeEventListener('click', handleClick);
    }
  });

  defineExpose({ show, hide, toggle });
</script>
