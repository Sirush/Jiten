<script setup lang="ts">
  import ProseA from '~/components/content/ProseA.vue';
  import { MARKDOWN_LINK_TARGET } from '~/utils/linkTarget';

  interface MdcNode {
    tag?: string;
    props?: Record<string, unknown>;
    children?: MdcNode[];
  }

  const props = defineProps<{
    source: string;
    newTab?: boolean;
  }>();

  provide(MARKDOWN_LINK_TARGET, props.newTab ? '_blank' : undefined);

  // MDC slugs a heading id per parse, so entries rendered on the same page collide (two updates
  // with "## Header" both claim #header). Dropping the ids also stops it wrapping headings in anchors,
  // which point at fragments that only mean anything inside one entry.
  function stripHeadingIds(node: MdcNode): MdcNode {
    const stripped: MdcNode = { ...node };

    if (stripped.tag && /^h[1-6]$/.test(stripped.tag) && stripped.props?.id) {
      stripped.props = { ...stripped.props };
      delete stripped.props.id;
    }

    if (Array.isArray(stripped.children)) {
      stripped.children = stripped.children.map(stripHeadingIds);
    }

    return stripped;
  }

  const components = { a: ProseA };
</script>

<template>
  <MDC :value="source">
    <template #default="{ body }">
      <MDCRenderer v-if="body" :body="stripHeadingIds(body)" :components="components" tag="div" class="prose break-words" />
    </template>
  </MDC>
</template>
