<script setup lang="ts">
  const emit = defineEmits<{ redeemed: [result: { tier: string; days: number; grantsFullTier: boolean }] }>();

  const { $api } = useNuxtApp();

  const code = ref('');
  const submitting = ref(false);
  const errorMessage = ref<string | null>(null);
  const success = ref<{ tier: string; days: number } | null>(null);

  async function redeem() {
    const trimmed = code.value.trim();
    if (!trimmed) return;

    submitting.value = true;
    errorMessage.value = null;
    success.value = null;
    try {
      const result = await $api<{ tier: string; days: number; grantsFullTier: boolean }>('/jiten-plus/redeem', {
        method: 'POST',
        body: { code: trimmed },
      });
      success.value = { tier: result.tier, days: result.days };
      code.value = '';
      emit('redeemed', result);
    } catch (e) {
      errorMessage.value = (e as { data?: { error?: string } })?.data?.error || 'This code could not be redeemed.';
    } finally {
      submitting.value = false;
    }
  }
</script>

<template>
  <div>
    <label for="promoCode" class="sr-only">Redeem a code</label>
    <div class="flex flex-col sm:flex-row gap-2">
      <InputText
        id="promoCode"
        v-model="code"
        placeholder="Enter your Jiten+ code"
        class="flex-1"
        :disabled="submitting"
        autocapitalize="characters"
        @keydown.enter="redeem"
      />
      <Button label="Redeem" icon="pi pi-gift" :loading="submitting" :disabled="!code.trim() || submitting" class="w-full sm:w-auto" @click="redeem" />
    </div>

    <Message v-if="success" severity="success" :closable="false" class="mt-3">
      Code redeemed! You now have Jiten+ {{ success.tier === 'full' ? 'Full' : 'Trial' }} for {{ success.days }} day{{ success.days === 1 ? '' : 's' }}.
    </Message>
    <Message v-if="errorMessage" severity="error" :closable="false" class="mt-3">
      {{ errorMessage }}
    </Message>
  </div>
</template>
