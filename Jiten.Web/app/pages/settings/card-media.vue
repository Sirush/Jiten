<script setup lang="ts">
  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Card media - Settings - Jiten' });

  const { isFull } = useJitenPlus();
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 flex flex-col gap-4">
    <div class="flex items-center gap-2">
      <NuxtLink to="/settings/subscription">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded />
      </NuxtLink>
      <h1 class="text-2xl font-bold">Card media</h1>
    </div>

    <Card>
      <template #content>
        <p v-if="!isFull" class="mb-4 flex items-start gap-2 text-sm text-amber-700 dark:text-amber-400">
          <i class="pi pi-exclamation-triangle mt-0.5 shrink-0" />
          <span>
            Without an active Jiten+ subscription you can view and delete your existing card media, but can't add new media. Resubscribe to add media again.
          </span>
        </p>
        <CardMediaManager />
      </template>
    </Card>
  </div>
</template>
