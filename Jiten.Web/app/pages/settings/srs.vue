<script setup lang="ts">
  import SettingsSrsStudy from '~/components/SettingsSrsStudy.vue';
  import SettingsFsrsParameters from '~/components/SettingsFsrsParameters.vue';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'SRS Settings' });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <SrsSubNav />
    <div class="flex flex-wrap items-center justify-between gap-2 mb-4 min-h-[2.5rem]">
      <h1 class="text-2xl font-bold">SRS Settings</h1>
    </div>
    <div class="flex flex-col gap-4">
      <SettingsSrsStudy />
      <SettingsFsrsParameters />
    </div>
  </div>
</template>
