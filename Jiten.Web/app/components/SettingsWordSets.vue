<script setup lang="ts">
  const { subscriptions, fetchSubscriptions } = useWordSets();
  const authStore = useAuthStore();

  const subscriptionCount = computed(() => subscriptions.value.length);

  const status = computed(() =>
    subscriptionCount.value > 0 ? `${subscriptionCount.value} active subscription${subscriptionCount.value === 1 ? '' : 's'}` : null,
  );

  onMounted(() => {
    if (authStore.isAuthenticated) {
      fetchSubscriptions();
    }
  });
</script>

<template>
  <SettingsTile
    icon="pi pi-tags"
    title="Word Sets"
    to="/settings/word-sets"
    description="Blacklist or master whole categories like names, places or particles. Recommended starting place for accurate coverage."
    :status="status"
  />
</template>
