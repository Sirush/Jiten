<script setup lang="ts">
  import Dialog from 'primevue/dialog';
  import OmniSearch from '~/components/OmniSearch.vue';

  const { isOpen, close } = useHeaderSearch();
  const route = useRoute();

  // OmniSearch navigates on its own, so the modal has to close itself once the route changes.
  watch(() => route.fullPath, close);
</script>

<template>
  <Dialog
    v-model:visible="isOpen"
    modal
    dismissable-mask
    :show-header="false"
    position="top"
    :style="{ width: '640px', maxWidth: '95vw' }"
    :pt="{
      content: { class: '!p-4 !overflow-visible' },
      root: { class: '!mt-16 sm:!mt-24 !overflow-visible' },
    }"
  >
    <!-- v-if remounts on each open, so the input never retains the previous query. -->
    <OmniSearch v-if="isOpen" autofocus :seed-from-route="false" />
  </Dialog>
</template>
