<!--
  Lightweight tooltip wrapper. On mount it only renders the slot and attaches
  hover/tap listeners — the expensive floating-ui positioning + teleported popup
  (TooltipPopup.vue) is mounted lazily on first interaction. This keeps pages
  that render many tooltips (e.g. deck lists with ~15 per card) cheap to mount.
-->
<template>
  <div
    ref="wrapperRef"
    :style="block ? undefined : { display: 'contents' }"
    @pointerenter="handlePointerEnter"
    @pointerleave="handlePointerLeave"
    @pointerdown.passive="handlePointerDown"
    @pointerup.passive="cancelLongPress"
    @pointercancel.passive="cancelLongPress"
    @focusin="handleFocusIn"
    @focusout="hide"
    @click.capture="swallowClickAfterLongPress"
    @click="handleClick"
    @contextmenu="handleContextMenu"
  >
    <slot :toggle="toggle" :show="show" :hide="hide" />
  </div>

  <TooltipPopup
    v-if="activated"
    :id="tooltipId"
    :content="content"
    :reference-el="referenceEl"
    :placement="placement"
    :offset="offset"
    :visible="isVisible"
    :opened-by-touch="openedByTouch"
    @request-hide="hide"
  />
</template>

<script lang="ts">
  const HOVER_DELAY_MS = 150;
  const WARM_WINDOW_MS = 300;
  const LONG_PRESS_MS = 500;
  const INTERACTIVE_SELECTOR =
    'a[href], button, input, select, textarea, summary, label, [role="button"], [role="link"], [role="menuitem"], [role="tab"], [role="checkbox"], [role="switch"], [tabindex]:not([tabindex="-1"])';

  /** Shared by every tooltip: moving to a neighbouring trigger inside this window skips the hover delay. */
  let warmUntil = 0;
</script>

<script setup lang="ts">
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

  const tooltipId = useId();
  const wrapperRef = ref<HTMLElement | null>(null);
  const referenceEl = shallowRef<HTMLElement | null>(null);
  const isVisible = ref(false);
  const openedByTouch = ref(false);
  // Becomes true on the first interaction, mounting TooltipPopup (and its
  // floating-ui setup) only then. Never reset, so a re-shown tooltip is instant.
  const activated = ref(false);

  let lastPointerType = '';
  let hoverTimer: ReturnType<typeof setTimeout> | undefined;
  let longPressTimer: ReturnType<typeof setTimeout> | undefined;
  let longPressed = false;
  let describedEl: HTMLElement | null = null;
  let describedByBefore: string | null = null;

  const describe = (el: HTMLElement) => {
    describedEl = el;
    describedByBefore = el.getAttribute('aria-describedby');
    el.setAttribute('aria-describedby', describedByBefore ? `${describedByBefore} ${tooltipId}` : tooltipId);
  };

  const undescribe = () => {
    if (!describedEl) return;
    if (describedByBefore) describedEl.setAttribute('aria-describedby', describedByBefore);
    else describedEl.removeAttribute('aria-describedby');
    describedEl = null;
  };

  const show = () => {
    clearTimeout(hoverTimer);
    if (!props.content || isVisible.value) return;
    const el = props.block ? wrapperRef.value : ((wrapperRef.value?.firstElementChild as HTMLElement | null) ?? null);
    if (!el) return;
    referenceEl.value = el;
    describe(el);
    activated.value = true;
    isVisible.value = true;
  };

  const hide = () => {
    clearTimeout(hoverTimer);
    if (!isVisible.value) return;
    isVisible.value = false;
    undescribe();
    warmUntil = performance.now() + WARM_WINDOW_MS;
  };

  const toggle = () => {
    if (isVisible.value) hide();
    else show();
  };

  const interactiveTrigger = (target: EventTarget | null) => {
    const hit = (target as Element | null)?.closest?.(INTERACTIVE_SELECTOR);
    return hit && wrapperRef.value?.contains(hit) ? hit : null;
  };

  const cancelLongPress = () => {
    clearTimeout(longPressTimer);
    longPressTimer = undefined;
  };

  const completeLongPress = () => {
    cancelLongPress();
    longPressed = true;
    openedByTouch.value = true;
    show();
  };

  /** Input type comes from each pointer event: maxTouchPoints and ontouchstart are true on desktops with a mouse (resistFingerprinting, touchscreen laptops). */
  const handlePointerEnter = (e: PointerEvent) => {
    if (e.pointerType === 'touch' || isVisible.value) return;
    openedByTouch.value = false;
    clearTimeout(hoverTimer);
    if (performance.now() < warmUntil) show();
    else hoverTimer = setTimeout(show, HOVER_DELAY_MS);
  };

  const handlePointerLeave = (e: PointerEvent) => {
    cancelLongPress();
    if (e.pointerType !== 'touch') hide();
  };

  /** Links keep the browser's own long-press menu, so they get no long-press tooltip. */
  const handlePointerDown = (e: PointerEvent) => {
    lastPointerType = e.pointerType;
    longPressed = false;
    cancelLongPress();
    if (e.pointerType !== 'touch') {
      clearTimeout(hoverTimer);
      return;
    }
    const trigger = interactiveTrigger(e.target);
    if (trigger && !trigger.matches('a[href]')) longPressTimer = setTimeout(completeLongPress, LONG_PRESS_MS);
  };

  const handleContextMenu = (e: Event) => {
    if (lastPointerType !== 'touch') return;
    if (longPressTimer) completeLongPress();
    if (longPressed) e.preventDefault();
  };

  /** Capture phase, so the release that ends a long press never reaches the trigger's own click handler. */
  const swallowClickAfterLongPress = (e: Event) => {
    if (!longPressed) return;
    longPressed = false;
    e.preventDefault();
    e.stopPropagation();
  };

  const handleClick = (e: Event) => {
    if (lastPointerType !== 'touch') return;
    if (interactiveTrigger(e.target)) {
      hide();
      return;
    }
    e.preventDefault();
    e.stopPropagation();
    openedByTouch.value = true;
    toggle();
  };

  /** :focus-visible keeps a mouse or touch press on a button from pinning the tooltip open. */
  const handleFocusIn = (e: FocusEvent) => {
    if (!(e.target as Element).matches(':focus-visible')) return;
    openedByTouch.value = false;
    show();
  };

  const handleEscape = (e: KeyboardEvent) => {
    if (e.key === 'Escape') hide();
  };

  watch(isVisible, (visible) => {
    if (visible) document.addEventListener('keydown', handleEscape);
    else document.removeEventListener('keydown', handleEscape);
  });

  onBeforeUnmount(() => {
    clearTimeout(hoverTimer);
    cancelLongPress();
    undescribe();
    document.removeEventListener('keydown', handleEscape);
  });

  defineExpose({ show, hide, toggle });
</script>
