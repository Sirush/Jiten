import type { Ref, ComputedRef } from 'vue';

interface SwipeGestureOptions {
  elementRef: Ref<HTMLElement | null>;
  isEnabled: ComputedRef<boolean>;
  isBusy: ComputedRef<boolean>;
  hapticFeedback?: ComputedRef<boolean>;
  onSwipeComplete: (direction: 'left' | 'right') => void;
  threshold?: number;
}

const DEAD_ZONE = 10;
const DEFAULT_THRESHOLD = 120;
const MAX_ROTATION = 6;
const ROTATION_FACTOR = 0.03;
const MAX_TRANSLATE_X = 15;
const MAX_TRANSLATE_Y = 5;
const SNAP_DURATION = 400;
const DISMISS_DURATION = 150;

export function useSwipeGesture(options: SwipeGestureOptions) {
  const threshold = options.threshold ?? DEFAULT_THRESHOLD;

  const offsetX = ref(0);
  const offsetY = ref(0);
  const rotation = ref(0);
  const cardOpacity = ref(1);
  const isDragging = ref(false);
  const isDismissing = ref(false);
  const isTransitioning = ref(false);
  const swipeDirection = ref<'left' | 'right' | null>(null);
  const swipeProgress = ref(0);

  let startX = 0;
  let startY = 0;
  let rawDx = 0;
  let committed = false;
  let scrolling = false;
  let activePointerId: number | null = null;
  let hapticFired = false;

  function onPointerDown(e: PointerEvent) {
    if (!options.isEnabled.value || options.isBusy.value || isDismissing.value) return;
    if (e.button !== 0) return; // left click / primary touch only

    const el = options.elementRef.value;
    if (!el) return;

    const target = e.target as HTMLElement | null;
    if (target?.closest('button, a, [role="tab"], [data-pc-name="tab"], [data-pc-name="tablist"]')) return;

    startX = e.clientX;
    startY = e.clientY;
    rawDx = 0;
    committed = false;
    scrolling = false;
    hapticFired = false;
    activePointerId = e.pointerId;
    document.addEventListener('pointermove', onPointerMove);
    document.addEventListener('pointerup', onPointerUp);
    document.addEventListener('pointercancel', onPointerCancel);
  }

  function onPointerMove(e: PointerEvent) {
    if (e.pointerId !== activePointerId) return;
    if (scrolling) return;

    const dx = e.clientX - startX;
    const dy = e.clientY - startY;

    if (!committed) {
      const dist = Math.sqrt(dx * dx + dy * dy);
      if (dist < DEAD_ZONE) return;

      if (Math.abs(dy) > Math.abs(dx)) {
        scrolling = true;
        activePointerId = null;
        removeDocListeners();
        return;
      }

      const sel = window.getSelection();
      if (sel && !sel.isCollapsed) {
        activePointerId = null;
        removeDocListeners();
        return;
      }

      committed = true;
      isDragging.value = true;
      const el = options.elementRef.value;
      if (el) el.setPointerCapture(e.pointerId);
    }

    e.preventDefault();

    rawDx = dx;
    const progress = Math.min(Math.abs(dx) / threshold, 1);
    const sign = dx > 0 ? 1 : -1;

    offsetX.value = sign * progress * MAX_TRANSLATE_X;
    offsetY.value = Math.max(-MAX_TRANSLATE_Y, Math.min(MAX_TRANSLATE_Y, dy * 0.05));
    rotation.value = Math.max(-MAX_ROTATION, Math.min(MAX_ROTATION, dx * ROTATION_FACTOR));
    swipeProgress.value = progress;
    swipeDirection.value = dx > 0 ? 'right' : dx < 0 ? 'left' : null;

    const pastThreshold = Math.abs(dx) >= threshold;
    if (pastThreshold && !hapticFired) {
      hapticFired = true;
      if (options.hapticFeedback?.value ?? true) navigator.vibrate?.(15);
    } else if (!pastThreshold) {
      hapticFired = false;
    }
  }

  function removeDocListeners() {
    document.removeEventListener('pointermove', onPointerMove);
    document.removeEventListener('pointerup', onPointerUp);
    document.removeEventListener('pointercancel', onPointerCancel);
  }

  function onPointerUp(e: PointerEvent) {
    if (e.pointerId !== activePointerId) return;
    activePointerId = null;
    removeDocListeners();

    if (!committed) {
      isDragging.value = false;
      return;
    }

    isDragging.value = false;

    if (Math.abs(rawDx) >= threshold) {
      dismiss(swipeDirection.value!);
    } else {
      snapBack();
    }
  }

  function onPointerCancel(e: PointerEvent) {
    if (e.pointerId !== activePointerId) return;
    activePointerId = null;
    removeDocListeners();
    if (committed) snapBack();
    isDragging.value = false;
  }

  function dismiss(direction: 'left' | 'right') {
    isDismissing.value = true;
    isTransitioning.value = true;

    const sign = direction === 'right' ? 1 : -1;
    offsetX.value = sign * 120;
    offsetY.value = sign * 18;
    rotation.value = sign * MAX_ROTATION * 2;
    cardOpacity.value = 0;

    setTimeout(() => {
      isTransitioning.value = false;
      options.onSwipeComplete(direction);
    }, DISMISS_DURATION);
  }

  function snapBack() {
    isTransitioning.value = true;
    offsetX.value = 0;
    offsetY.value = 0;
    rotation.value = 0;
    swipeProgress.value = 0;
    swipeDirection.value = null;

    setTimeout(() => {
      isTransitioning.value = false;
    }, SNAP_DURATION);
  }

  function resetInstant() {
    offsetX.value = 0;
    offsetY.value = 0;
    rotation.value = 0;
    cardOpacity.value = 1;
    swipeProgress.value = 0;
    swipeDirection.value = null;
    isDragging.value = false;
    isDismissing.value = false;
  }

  watch(options.elementRef, (el, oldEl) => {
    if (oldEl) oldEl.removeEventListener('pointerdown', onPointerDown);
    if (el) el.addEventListener('pointerdown', onPointerDown);
  });

  onUnmounted(() => {
    const el = options.elementRef.value;
    if (el) el.removeEventListener('pointerdown', onPointerDown);
    removeDocListeners();
  });

  return {
    offsetX: readonly(offsetX),
    offsetY: readonly(offsetY),
    rotation: readonly(rotation),
    cardOpacity: readonly(cardOpacity),
    isDragging: readonly(isDragging),
    isDismissing: readonly(isDismissing),
    isTransitioning: readonly(isTransitioning),
    swipeDirection: readonly(swipeDirection),
    swipeProgress: readonly(swipeProgress),
    resetInstant,
  };
}
