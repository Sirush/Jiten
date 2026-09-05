<script setup lang="ts">
  import OmniSearch from '~/components/OmniSearch.vue';
  import HomeTryChips from './HomeTryChips.vue';
  import HomeWhatsNewStrip from './HomeWhatsNewStrip.vue';
  import HomePollCard from './HomePollCard.vue';
  import HomePlusBlock from './HomePlusBlock.vue';
  import { homeStrips } from './homeStrips';

  useHead({ title: 'Jiten' });
</script>

<template>
  <div class="container mx-auto px-4 pt-2 pb-6">
    <div class="max-w-3xl mx-auto flex flex-col">
      <div class="order-2 sm:order-1 mb-4">
        <h1 class="sr-only">Jiten</h1>
        <OmniSearch autofocus placeholder="Search words, titles, or describe something you're interested in" />
        <HomeTryChips class="mt-2" />
      </div>

      <div class="order-1 sm:order-2 mb-4">
        <HomeStudyBanner />
      </div>

      <div class="order-3 flex flex-col gap-4">
        <ClientOnly>
          <LegalUpdateBanner />
        </ClientOnly>

        <div
          class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm overflow-hidden divide-y divide-surface-200 dark:divide-surface-700"
        >
          <HomeWhatsNewStrip compact />
          <HomePollCard variant="row" />
          <HomePlusBlock compact />
        </div>
        <HomePollCard variant="card" />

        <component :is="strip.component" v-for="strip in homeStrips" :key="strip.id" />
      </div>
    </div>
  </div>
</template>
