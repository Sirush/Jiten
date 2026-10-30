<script setup lang="ts">
  definePageMeta({
    redirect: (to) => ({ path: `/profile/${encodeURIComponent(String(to.params.username))}/library`, query: to.query }),
  });
</script>

<template>
  <div />
</template>
