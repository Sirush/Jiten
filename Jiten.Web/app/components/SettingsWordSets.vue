<script setup lang="ts">
const { subscriptions, fetchSubscriptions } = useWordSets();
const authStore = useAuthStore();

const subscriptionCount = computed(() => subscriptions.value.length);

onMounted(() => {
  if (authStore.isAuthenticated) {
    fetchSubscriptions();
  }
});
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">Word Sets</h3>
    </template>
    <template #content>
      <p class="mb-3">
        Bulk-manage vocabulary with word sets. Blacklist entire categories like names or places, or mark common words as mastered
        without creating individual cards.
      </p>
      <p v-if="subscriptionCount > 0" class="mb-3 text-muted-color">
        You have {{ subscriptionCount }} active subscription{{ subscriptionCount === 1 ? '' : 's' }}.
      </p>
      <NuxtLink to="/settings/word-sets">
        <Button icon="pi pi-cog" label="Manage Word Sets" class="w-full md:w-64" />
      </NuxtLink>
    </template>
  </Card>
</template>
