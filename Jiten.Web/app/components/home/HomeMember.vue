<script setup lang="ts">
  import OmniSearch from '~/components/OmniSearch.vue';
  import { homeStrips } from './homeStrips';

  useHead({ title: 'Jiten' });
</script>

<template>
  <div class="container mx-auto px-4 pt-2 pb-6">
    <div class="max-w-3xl mx-auto flex flex-col">
      <div class="order-2 sm:order-1 mb-4">
        <h1 class="sr-only">Jiten</h1>
        <OmniSearch />
      </div>

      <div class="order-1 sm:order-2 mb-4">
        <HomeStudyBanner />
      </div>

      <div class="order-3 flex flex-col gap-4">
        <component :is="strip.component" v-for="strip in homeStrips" :key="strip.id" />
      </div>
    </div>
  </div>
</template>
