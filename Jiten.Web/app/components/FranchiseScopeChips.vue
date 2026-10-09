<script setup lang="ts">
  import { mediaGroupKindWord, type ScopeChip } from '~/utils/mediaGroup';
  import { franchiseChipClass } from '~/utils/franchiseChip';

  const props = defineProps<{
    chips: ScopeChip[];
    activeKey: string | null;
  }>();

  const emit = defineEmits<{ select: [chip: ScopeChip] }>();

  const COLLAPSED_GROUPS = 6;
  const showAll = ref(false);

  const groupChips = computed(() => props.chips.filter((c) => c.scope));
  const collapsible = computed(() => groupChips.value.length > COLLAPSED_GROUPS);
  const visibleChips = computed(() => {
    if (showAll.value || !collapsible.value) return props.chips;
    const keep = new Set(groupChips.value.slice(0, COLLAPSED_GROUPS).map((c) => c.key));
    return props.chips.filter((c) => !c.scope || keep.has(c.key) || c.key === props.activeKey);
  });
  const hiddenCount = computed(() => props.chips.length - visibleChips.value.length);

  const chipTitle = (chip: ScopeChip) => (chip.scope ? `${mediaGroupKindWord(chip.scope.kind)}: ${chip.label}` : chip.label);
</script>

<template>
  <div
    v-if="chips.length > 1"
    role="group"
    aria-label="Part of the franchise"
    class="flex min-w-0 flex-wrap gap-2 max-sm:-mx-1 max-sm:-my-1 max-sm:flex-nowrap max-sm:overflow-x-auto max-sm:px-1 max-sm:py-1 max-sm:[scrollbar-width:none] max-sm:*:shrink-0"
  >
    <button
      v-for="chip in visibleChips"
      :key="chip.key"
      type="button"
      :aria-pressed="chip.key === activeKey"
      :title="chipTitle(chip)"
      :class="franchiseChipClass(chip.key === activeKey)"
      @click="emit('select', chip)"
    >
      <span v-if="chip.scope" class="sr-only">{{ mediaGroupKindWord(chip.scope.kind) }}:</span>
      <span class="max-w-[16rem] truncate" v-bind="japaneseTextAttrs(chip.label)">{{ chip.label }}</span>
      <span class="font-normal tabular-nums text-surface-600 dark:text-surface-300">{{ chip.deckIds.length }}</span>
    </button>
    <button
      v-if="collapsible && (showAll || hiddenCount > 0)"
      type="button"
      class="min-h-11 whitespace-nowrap rounded-md px-2 text-sm text-primary hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 sm:pointer-fine:min-h-9"
      :aria-expanded="showAll"
      @click="showAll = !showAll"
    >
      {{ showAll ? 'Fewer series' : `${hiddenCount} more series` }}
    </button>
  </div>
</template>
