<script setup lang="ts">
  interface MdcNode {
    tag?: string;
    props?: Record<string, unknown>;
    children?: MdcNode[];
  }

  defineProps<{
    source: string;
  }>();

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
</script>

<template>
  <MDC :value="source">
    <template #default="{ body }">
      <MDCRenderer v-if="body" :body="stripHeadingIds(body)" tag="div" class="prose" />
    </template>
  </MDC>
</template>
