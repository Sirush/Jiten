<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { type ApiKeyInfo, type CreateApiKeyResponse } from '~/types/types';

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const apiKeyInfo = ref<ApiKeyInfo | null>(null);
  const newlyCreatedKey = ref<string | null>(null);
  const isLoading = ref(false);
  const isCopied = ref(false);

  const fetchApiKeyInfo = async () => {
    try {
      const result = await $api<{ apiKey: ApiKeyInfo | null }>('api-key/info');
      apiKeyInfo.value = result.apiKey;
    } catch {
      apiKeyInfo.value = null;
    }
  };

  onMounted(fetchApiKeyInfo);

  const hasLiveKey = computed(() => !!apiKeyInfo.value && !apiKeyInfo.value.isRevoked);

  const status = computed(() => {
    if (!hasLiveKey.value) return null;
    const lastUsed = apiKeyInfo.value!.lastUsedAt ? formatDateShort(apiKeyInfo.value!.lastUsedAt) : 'never used';
    return `Created ${formatDateShort(apiKeyInfo.value!.createdAt)} - last used ${lastUsed}`;
  });

  const createApiKey = async () => {
    try {
      isLoading.value = true;
      const result = await $api<CreateApiKeyResponse>('api-key/create', { method: 'POST' });
      newlyCreatedKey.value = result.apiKey;
      await fetchApiKeyInfo();
      toast.add({
        severity: 'success',
        summary: 'API key created',
        detail: 'Your API key has been created. Make sure to copy it now!',
        life: 10000,
      });
    } catch (error: unknown) {
      const errorMessage = error instanceof Error ? error.message : 'Failed to create API key';
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: errorMessage,
        life: 5000,
      });
    } finally {
      isLoading.value = false;
    }
  };

  const revokeAndRegenerate = async () => {
    if (!hasLiveKey.value) return;

    try {
      isLoading.value = true;

      await $api(`api-key/${apiKeyInfo.value!.id}/revoke`, { method: 'POST' });

      const result = await $api<CreateApiKeyResponse>('api-key/create', { method: 'POST' });
      newlyCreatedKey.value = result.apiKey;
      await fetchApiKeyInfo();
      toast.add({
        severity: 'success',
        summary: 'API key regenerated',
        detail: 'Your old API key has been revoked and a new one created.',
        life: 10000,
      });
    } catch (error: unknown) {
      const errorMessage = error instanceof Error ? error.message : 'Failed to regenerate API key';
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: errorMessage,
        life: 5000,
      });
    } finally {
      isLoading.value = false;
    }
  };

  const confirmGenerate = () => {
    confirm.require({
      message: 'Your API key will only be shown once after creation. Make sure to copy it immediately.',
      header: 'Generate API Key',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: {
        label: 'Cancel',
        severity: 'secondary',
        outlined: true,
      },
      acceptProps: {
        label: 'Generate',
      },
      accept: async () => {
        await createApiKey();
      },
    });
  };

  const confirmRegenerate = () => {
    confirm.require({
      message: 'This will revoke your current API key immediately. Any applications using the old key will stop working. The new key will only be shown once.',
      header: 'Regenerate API Key',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: {
        label: 'Cancel',
        severity: 'secondary',
        outlined: true,
      },
      acceptProps: {
        label: 'Regenerate',
        severity: 'danger',
      },
      accept: async () => {
        await revokeAndRegenerate();
      },
    });
  };

  const copyToClipboard = async () => {
    if (!newlyCreatedKey.value) return;
    try {
      await navigator.clipboard.writeText(newlyCreatedKey.value);
      isCopied.value = true;
      toast.add({
        severity: 'success',
        summary: 'Copied',
        detail: 'API key copied to clipboard',
        life: 3000,
      });
      setTimeout(() => {
        isCopied.value = false;
      }, 2000);
    } catch {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to copy to clipboard',
        life: 3000,
      });
    }
  };
</script>

<template>
  <SettingsTile
    icon="pi pi-key"
    title="API Key"
    description="Authenticate third-party apps. Anyone holding the key can read all your information, so make sure it's a trusted source."
    :status="status"
  >
    <Message v-if="newlyCreatedKey" severity="warn" :closable="false" class="mt-3">
      <p class="mb-2 font-semibold">Your new API key (only shown once):</p>
      <div class="flex items-center gap-2">
        <code class="flex-1 rounded bg-surface-100 p-2 text-sm break-all dark:bg-surface-800">{{ newlyCreatedKey }}</code>
        <Button
          :icon="isCopied ? 'pi pi-check' : 'pi pi-copy'"
          :severity="isCopied ? 'success' : 'secondary'"
          :aria-label="isCopied ? 'API key copied' : 'Copy API key'"
          @click="copyToClipboard"
        />
      </div>
    </Message>

    <Button
      v-if="hasLiveKey"
      icon="pi pi-refresh"
      label="Regenerate"
      severity="warn"
      size="small"
      outlined
      class="mt-3"
      :loading="isLoading"
      :disabled="isLoading"
      @click="confirmRegenerate"
    />
    <Button v-else icon="pi pi-key" label="Generate API key" size="small" outlined class="mt-3" :loading="isLoading" @click="confirmGenerate" />
  </SettingsTile>
</template>
