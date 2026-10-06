<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';

  const props = defineProps<{
    surface: string;
  }>();

  const auth = useAuthStore();
  const { isDismissed, dismiss, authLink } = useGuestPrompt();

  const mounted = ref(false);
  onMounted(() => (mounted.value = true));
  const visible = computed(() => mounted.value && !auth.isAuthenticated && !isDismissed(props.surface));

  let shownTracked = false;
  watch(
    visible,
    (isVisible) => {
      if (!isVisible || shownTracked) return;
      shownTracked = true;
      trackEvent('guest_strip_shown', { surface: props.surface });
    },
    { immediate: true }
  );

  function onCreate() {
    markGuestPrompt(`strip_${props.surface}`);
    trackEvent('guest_strip_clicked', { surface: props.surface });
  }

  function onDismiss() {
    dismiss(props.surface);
    trackEvent('guest_strip_dismissed', { surface: props.surface });
  }
</script>

<template>
  <div
    v-if="visible"
    class="flex items-center gap-3 rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 py-2 pl-3 pr-1"
  >
    <span
      class="shrink-0 hidden sm:flex items-center justify-center w-8 h-8 rounded-lg bg-primary-50 dark:bg-primary-900/40 text-primary-600 dark:text-primary-300"
    >
      <Icon name="material-symbols:person-add-outline" size="1.2em" />
    </span>
    <p class="flex-1 min-w-0 text-sm text-gray-700 dark:text-gray-300">
      <slot />
      <NuxtLink
        :to="authLink('/register')"
        class="ml-1 inline-block font-semibold text-primary-700 dark:text-primary-300 hover:underline whitespace-nowrap"
        @click="onCreate"
        >Create an account</NuxtLink
      >
    </p>
    <button
      type="button"
      class="shrink-0 inline-flex items-center justify-center w-11 h-11 rounded-lg text-gray-500 dark:text-gray-400 hover:bg-surface-100 dark:hover:bg-surface-800 focus-visible:outline-2 focus-visible:outline-primary-500"
      aria-label="Dismiss"
      @click="onDismiss"
    >
      <Icon name="material-symbols:close" size="1.2em" />
    </button>
  </div>
</template>
