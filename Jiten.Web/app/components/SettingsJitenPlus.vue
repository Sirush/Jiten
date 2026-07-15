<script setup lang="ts">
  const { tier, sources, loading } = useJitenPlus();

  const tierBadgeTier = computed<'any' | 'full'>(() => (tier.value === 'full' ? 'full' : 'any'));

  const detailLine = computed(() => {
    const s = sources.value;
    if (!s) return 'Support Jiten and unlock a few extras.';
    if (s.isLifetime) return 'You have lifetime access.';
    if (s.subscriptionActive) {
      const plan = s.plan ? s.plan.toLowerCase() : 'active';
      return `Active ${plan} subscription.`;
    }
    if (tier.value === 'trial') {
      return `Jiten+ Trial — ${s.promoCreditDays} day${s.promoCreditDays === 1 ? '' : 's'} left · includes everything except permanent-storage features.`;
    }
    if (s.adminOverride) return 'Access granted by an administrator.';
    if (s.promoCreditDays > 0) return `${s.promoCreditDays} day${s.promoCreditDays === 1 ? '' : 's'} left.`;
    return 'Support Jiten and unlock a few extras. The core platform is always free.';
  });
</script>

<template>
  <Card>
    <template #title>
      <div class="flex items-center gap-2">
        <h3 class="text-lg font-semibold">Jiten+</h3>
      </div>
    </template>
    <template #content>
      <p class="text-gray-600 dark:text-gray-300 mb-3">{{ detailLine }}</p>
      <NuxtLink to="/settings/subscription">
        <Button icon="pi pi-star" label="Manage Jiten+" class="w-full md:w-64" />
      </NuxtLink>
    </template>
  </Card>
</template>
