<script setup lang="ts">
  const { tier, sources } = useJitenPlus();

  const detailLine = computed(() => {
    const s = sources.value;
    if (!s) return 'Support Jiten and unlock a few extras.';
    if (s.isLifetime) return 'You have lifetime access.';
    if (s.subscriptionActive) {
      const plan = s.plan ? s.plan.toLowerCase() : 'active';
      return `Active ${plan} subscription.`;
    }
    if (tier.value === 'trial') {
      return `Jiten+ Trial - ${s.promoCreditDays} day${s.promoCreditDays === 1 ? '' : 's'} left.`;
    }
    if (s.adminOverride) return 'Access granted by an administrator.';
    if (s.promoCreditDays > 0) return `${s.promoCreditDays} day${s.promoCreditDays === 1 ? '' : 's'} left.`;
    return 'Support Jiten and unlock a few extras.';
  });
</script>

<template>
  <SettingsTile icon="pi pi-star" title="Jiten+" to="/settings/subscription" description="Subscription, billing and perks." :status="detailLine" />
</template>
