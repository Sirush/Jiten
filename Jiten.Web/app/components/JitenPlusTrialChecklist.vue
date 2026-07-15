<script setup lang="ts">
  import Dialog from 'primevue/dialog';
  import Button from 'primevue/button';

  const visible = defineModel<boolean>('visible', { default: false });

  const included = ['Custom frequency lists (generate & download)', '5 media request boosts each month', 'And everything free stays free'];

  const notIncluded = ['Card images & audio uploads (permanently stored, 10 GB)', 'Saving frequency lists (auto-update + public links)'];
</script>

<template>
  <Dialog v-model:visible="visible" modal :draggable="false" header="Welcome to your Jiten+ trial" class="w-full max-w-2xl mx-3">
    <p class="text-surface-700 dark:text-surface-200 leading-relaxed mb-5">
      The trial includes every Jiten+ feature except the ones that permanently store your data. Those unlock with any paid plan.
    </p>

    <div class="grid gap-4 sm:grid-cols-2">
      <div class="rounded-lg border border-green-200 dark:border-green-800/60 bg-green-50/60 dark:bg-green-950/30 p-4">
        <h3 class="flex items-center gap-2 font-semibold text-green-700 dark:text-green-300 mb-3">
          <Icon name="material-symbols:check-circle-rounded" />
          Included in your trial
        </h3>
        <ul class="space-y-2 text-sm text-surface-700 dark:text-surface-200">
          <li v-for="item in included" :key="item" class="flex items-start gap-2">
            <Icon name="material-symbols:check-small-rounded" class="text-green-500 mt-0.5 flex-shrink-0" />
            <span>{{ item }}</span>
          </li>
        </ul>
      </div>

      <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-50 dark:bg-surface-800/40 p-4">
        <h3 class="flex items-center gap-2 font-semibold text-surface-700 dark:text-surface-200 mb-3">
          <Icon name="material-symbols:lock-outline-rounded" />
          Not in the trial (Full only)
        </h3>
        <ul class="space-y-2 text-sm text-surface-600 dark:text-surface-300">
          <li v-for="item in notIncluded" :key="item" class="flex items-start gap-2">
            <Icon name="material-symbols:lock-outline-rounded" class="text-surface-400 mt-0.5 flex-shrink-0" />
            <span>{{ item }}</span>
          </li>
        </ul>
      </div>
    </div>

    <p class="text-sm text-surface-600 dark:text-surface-300 mt-5">Full unlocks with any paid plan. Anything you upload stays safe even if you cancel later.</p>

    <template #footer>
      <NuxtLink to="/jiten-plus" class="mr-auto">
        <Button label="See plans" text />
      </NuxtLink>
      <Button label="Got it" @click="visible = false" />
    </template>
  </Dialog>
</template>
