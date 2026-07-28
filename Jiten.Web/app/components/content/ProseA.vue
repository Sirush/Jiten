<script setup lang="ts">
  // `rel` is declared as a prop so the parser's injected rel="nofollow" can't fall through and win.
  const props = defineProps<{ href?: string; target?: string; rel?: string }>();

  const isExternal = computed(() => /^(https?:)?\/\//i.test(props.href ?? ''));
</script>

<template>
  <a v-if="isExternal" :href="href" :target="target || '_blank'" rel="nofollow noopener noreferrer"><slot /></a>
  <NuxtLink v-else :to="href" :target="target" :rel="rel"><slot /></NuxtLink>
</template>
