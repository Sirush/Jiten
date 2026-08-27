<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import Textarea from 'primevue/textarea';
  import ToggleSwitch from 'primevue/toggleswitch';
  import { useToast } from 'primevue/usetoast';

  useHead({ title: 'Maintenance Banner - Jiten' });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  const { $api } = useNuxtApp();
  const toast = useToast();

  const isActive = ref(false);
  const message = ref('');
  const saving = ref(false);

  onMounted(async () => {
    try {
      const data = await $api<{ isActive: boolean; message: string | null }>('/maintenance/banner');
      isActive.value = data.isActive;
      message.value = data.message ?? '';
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to load banner state'), life: 5000 });
    }
  });

  async function saveBanner() {
    try {
      saving.value = true;
      await $api('/maintenance/banner', {
        method: 'POST',
        body: { isActive: isActive.value, message: message.value },
      });
      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: isActive.value ? 'Maintenance banner activated' : 'Maintenance banner deactivated',
        life: 5000,
      });
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to update banner'), life: 5000 });
    } finally {
      saving.value = false;
    }
  }
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Maintenance Banner</h1>
    </div>

    <Card class="shadow-md max-w-xl">
      <template #content>
        <div class="flex flex-col gap-4">
          <div class="flex items-center gap-3">
            <ToggleSwitch v-model="isActive" />
            <label class="text-sm font-medium">{{ isActive ? 'Banner active' : 'Banner inactive' }}</label>
          </div>

          <div>
            <label for="bannerMessage" class="block text-sm font-medium mb-1">Message</label>
            <Textarea
              id="bannerMessage"
              v-model="message"
              rows="3"
              class="w-full"
              placeholder="e.g. Maintenance in progress: your coverage might not work correctly."
            />
          </div>

          <div class="flex justify-end">
            <Button label="Save" icon="pi pi-check" class="p-button-primary" :loading="saving" :disabled="saving" @click="saveBanner" />
          </div>
        </div>
      </template>
    </Card>
  </div>
</template>
