<!--
  Heavy half of Tooltip: floating-ui positioning + the teleported popup.
  Split out of Tooltip.vue and mounted lazily (only after the first hover/tap)
  so that lists rendering hundreds of tooltips don't pay the useFloating setup
  cost up front. See Tooltip.vue.
-->
<template>
  <Teleport v-if="formattedContent != ''" to="body">
    <Transition
      enter-active-class="transition-opacity duration-200"
      enter-from-class="opacity-0"
      enter-to-class="opacity-100"
      leave-active-class="transition-opacity duration-150"
      leave-from-class="opacity-100"
      leave-to-class="opacity-0"
    >
      <div
        v-if="visible"
        ref="floatingRef"
        :style="{
          position: strategy,
          top: `${y ?? 0}px`,
          left: `${x ?? 0}px`,
        }"
        class="z-[1200]"
      >
        <div class="bg-gray-900 dark:bg-gray-800 text-white px-3 py-2 rounded-lg shadow-lg text-sm max-w-sm">
          <div class="whitespace-pre-wrap" v-html="formattedContent" />
        </div>

        <!-- Arrow -->
        <div
          ref="arrowRef"
          :style="{
            position: 'absolute',
            left: arrowX != null ? `${arrowX}px` : '',
            top: arrowY != null ? `${arrowY}px` : '',
            ...arrowStyle,
          }"
          class="w-0 h-0"
        >
          <div class="absolute border-transparent" :class="arrowClasses" />
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<script setup lang="ts">
  import { ref, computed, toRef, watch, onBeforeUnmount } from 'vue';
  import { useFloating, autoUpdate, offset as offsetMiddleware, flip, shift, arrow } from '@floating-ui/vue';

  interface Props {
    content: string;
    referenceEl: HTMLElement | null;
    placement?: 'top' | 'bottom' | 'left' | 'right';
    offset?: number;
    visible?: boolean;
    isMobile?: boolean;
  }

  const props = withDefaults(defineProps<Props>(), {
    placement: 'top',
    offset: 8,
    visible: false,
    isMobile: false,
  });

  const emit = defineEmits<{ requestHide: [] }>();

  const referenceEl = toRef(props, 'referenceEl');
  const floatingRef = ref<HTMLElement | null>(null);
  const arrowRef = ref<HTMLElement | null>(null);

  const formattedContent = computed(() => {
    const html = props.content.replace(/\*\*(.*?)\*\*/g, '<strong class="font-semibold">$1</strong>').replace(/\n/g, '<br>');
    return sanitiseHtml(html);
  });

  const {
    x,
    y,
    strategy,
    middlewareData,
    placement: computedPlacement,
  } = useFloating(referenceEl, floatingRef, {
    placement: props.placement,
    middleware: [offsetMiddleware(props.offset), flip(), shift({ padding: 8 }), arrow({ element: arrowRef })],
    whileElementsMounted: autoUpdate,
  });

  const arrowX = computed(() => middlewareData.value.arrow?.x);
  const arrowY = computed(() => middlewareData.value.arrow?.y);

  const arrowStyle = computed(() => {
    const side = computedPlacement.value.split('-')[0]
    switch (side) {
      case 'top':
        return { bottom: '-6px' }
      case 'bottom':
        return { top: '-6px' }
      case 'left':
        return { right: '-6px' }
      case 'right':
        return { left: '-6px' }
      default:
        return {}
    }
  })

  const arrowClasses = computed(() => {
    const side = computedPlacement.value.split('-')[0]
    const baseColor = 'border-gray-900 dark:border-gray-800'

    switch (side) {
      case 'bottom':
        return `${baseColor} border-b-8 border-x-8 border-x-transparent border-b-gray-900 dark:border-b-gray-800 top-0`
      case 'top':
        return `${baseColor} border-t-8 border-x-8 border-x-transparent border-t-gray-900 dark:border-t-gray-800 bottom-0`
      case 'right':
        return `${baseColor} border-r-8 border-y-8 border-y-transparent border-r-gray-900 dark:border-r-gray-800 left-0`
      case 'left':
        return `${baseColor} border-l-8 border-y-8 border-y-transparent border-l-gray-900 dark:border-l-gray-800 right-0`
      default:
        return ''
    }
  })

  // Mobile dismissal: tapping outside the trigger or popup closes it.
  const handleClickOutside = (e: Event) => {
    if (
      props.referenceEl &&
      floatingRef.value &&
      !props.referenceEl.contains(e.target as Node) &&
      !floatingRef.value.contains(e.target as Node)
    ) {
      emit('requestHide');
    }
  };

  // immediate so the listener attaches on the first open too: this component mounts
  // lazily with visible already true, so a lazy watcher would miss that first value
  // and leave the very first tooltip un-dismissable by an outside tap.
  watch(
    () => props.visible,
    (visible) => {
      if (visible && props.isMobile) {
        requestAnimationFrame(() => document.addEventListener('click', handleClickOutside));
      } else {
        document.removeEventListener('click', handleClickOutside);
      }
    },
    { immediate: true }
  );

  onBeforeUnmount(() => {
    document.removeEventListener('click', handleClickOutside);
  });
</script>
