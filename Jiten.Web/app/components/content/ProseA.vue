<script setup lang="ts">
  import { classifyLink, MARKDOWN_LINK_TARGET } from '~/utils/linkTarget';

  // `rel` is declared as a prop so the parser's injected rel="nofollow" can't fall through and win.
  const props = defineProps<{ href?: string; target?: string; rel?: string }>();

  const link = computed(() => classifyLink(props.href));
  const defaultTarget = inject(MARKDOWN_LINK_TARGET, undefined);
  const resolvedTarget = computed(() => props.target || (link.value.kind === 'internal' && link.value.to.startsWith('#') ? undefined : defaultTarget));
</script>

<template>
  <NuxtLink v-if="link.kind === 'internal'" :to="link.to" :target="resolvedTarget"><slot /></NuxtLink>
  <a v-else-if="link.kind === 'external'" :href="link.href" :target="resolvedTarget || '_blank'" rel="nofollow noopener noreferrer"><slot /></a>
  <a v-else-if="link.kind === 'protocol'" :href="link.href"><slot /></a>
  <span v-else><slot /></span>
</template>
