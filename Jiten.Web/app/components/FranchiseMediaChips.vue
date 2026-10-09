<script setup lang="ts">
  import type { FranchiseNode, MediaType } from '~/types';
  import { getMediaTypePluralText } from '~/utils/mediaTypeMapper';
  import { franchiseChipClass } from '~/utils/franchiseChip';
  import { countByMediaType } from '~/utils/mediaGroup';

  const props = defineProps<{
    members: FranchiseNode[];
  }>();

  const selected = defineModel<MediaType[]>({ required: true });

  const chips = computed(() =>
    [...countByMediaType(props.members).entries()].sort((a, b) => b[1] - a[1] || a[0] - b[0]).map(([mediaType, deckCount]) => ({ mediaType, deckCount }))
  );

  const isSelected = (type: MediaType) => selected.value.includes(type);

  function toggle(type: MediaType) {
    selected.value = isSelected(type) ? selected.value.filter((t) => t !== type) : [...selected.value, type];
  }
</script>

<template>
  <div
    v-if="chips.length > 1"
    role="group"
    aria-label="Filter by media type"
    class="flex min-w-0 flex-wrap gap-2 max-sm:-mx-1 max-sm:-my-1 max-sm:flex-nowrap max-sm:overflow-x-auto max-sm:px-1 max-sm:py-1 max-sm:[scrollbar-width:none] max-sm:*:shrink-0 max-sm:*:whitespace-nowrap"
  >
    <button type="button" :aria-pressed="selected.length === 0" :class="franchiseChipClass(selected.length === 0)" @click="selected = []">All media</button>
    <button
      v-for="chip in chips"
      :key="chip.mediaType"
      type="button"
      :aria-pressed="isSelected(chip.mediaType)"
      :class="franchiseChipClass(isSelected(chip.mediaType))"
      @click="toggle(chip.mediaType)"
    >
      {{ getMediaTypePluralText(chip.mediaType) }}
      <span class="font-normal tabular-nums text-surface-600 dark:text-surface-300">{{ chip.deckCount }}</span>
    </button>
  </div>
</template>
