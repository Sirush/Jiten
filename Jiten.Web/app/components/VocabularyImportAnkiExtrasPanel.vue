<script setup lang="ts">
  import AnkiConnectImport from '~/components/AnkiConnectImport.vue';

  const emit = defineEmits<{ changed: [] }>();
</script>

<template>
  <AnkiConnectImport media-only @import-complete="emit('changed')" />
</template>
