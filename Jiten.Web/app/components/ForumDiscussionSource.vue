<script setup lang="ts">
  /** The forum's embed crawler builds a new topic's first post only from inside this element (Discourse `embed_allowlist_selector` is `[data-forum-source]`). */
  const crawlerScope = { 'data-forum-source': '' };
</script>

<template>
  <div v-bind="crawlerScope">
    <slot />
  </div>
</template>
