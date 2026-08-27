<script setup lang="ts">
  const { $api } = useNuxtApp();

  interface BannerState {
    isActive: boolean;
    message: string | null;
    updatedAt: string | null;
  }

  const banner = ref<BannerState | null>(null);
  const dismissed = ref(false);

  const storageKey = 'maintenance-dismissed';

  onMounted(async () => {
    try {
      banner.value = await $api<BannerState>('/maintenance/banner');
      if (banner.value?.isActive && banner.value.updatedAt) {
        const stored = localStorage.getItem(storageKey);
        if (stored === banner.value.updatedAt) {
          dismissed.value = true;
        }
      }
    } catch {
      // Silently fail
    }
  });

  function dismiss() {
    dismissed.value = true;
    if (banner.value?.updatedAt) {
      localStorage.setItem(storageKey, banner.value.updatedAt);
    }
  }

  const showBanner = computed(() => banner.value?.isActive && banner.value?.message && !dismissed.value);
</script>

<template>
  <div v-if="showBanner" class="bg-red-600 text-white px-4 py-3 text-center text-sm relative" role="alert">
    <span>{{ banner!.message }}</span>
    <button
      class="absolute right-3 top-1/2 -translate-y-1/2 text-white hover:text-red-200 text-lg leading-none cursor-pointer"
      aria-label="Dismiss"
      @click="dismiss"
    >
      &times;
    </button>
  </div>
</template>
