<script setup lang="ts">
  const props = defineProps<{
    previousLink: object | null;
    nextLink: object | null;
    start: number;
    end: number;
    totalItems: number;
    itemLabel?: string;
    scrollToTopOnNext?: boolean;
  }>();

  const label = computed(() => props.itemLabel ?? 'items');

  const scrollToTop = () => {
    nextTick(() => {
      window.scrollTo({ top: 0, behavior: 'instant' });
    });
  };
</script>

<template>
  <div class="flex justify-between flex-col md:flex-row">
    <div class="flex gap-8 pl-2">
      <NuxtLink :to="previousLink" :class="previousLink == null ? 'text-gray-500! pointer-events-none' : ''" :aria-disabled="previousLink == null ? 'true' : undefined" no-rel>
        Previous
      </NuxtLink>
      <NuxtLink
        :to="nextLink"
        :class="nextLink == null ? 'text-gray-500! pointer-events-none' : ''"
        :aria-disabled="nextLink == null ? 'true' : undefined"
        no-rel
        @click="scrollToTopOnNext ? scrollToTop() : undefined"
      >
        Next
      </NuxtLink>
    </div>
    <div class="pr-2 text-gray-500 dark:text-gray-300">
      viewing {{ label }} {{ start }}-{{ end }} from {{ totalItems }} total
    </div>
  </div>
</template>
