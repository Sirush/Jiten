<script setup lang="ts">
  import { vocabularyTierOptions, type VocabularyModifierMode } from '~/composables/useVocabularyDisplayFilter';

  const tiers = defineModel<string[]>('tiers', { required: true });
  const suspended = defineModel<VocabularyModifierMode>('suspended', { required: true });
  const redundant = defineModel<VocabularyModifierMode>('redundant', { required: true });

  const popover = ref();

  const modeOptions = [
    { label: 'Show', value: 'show' },
    { label: 'Hide', value: 'hide' },
    { label: 'Only', value: 'only' },
  ];

  const label = computed(() => {
    if (tiers.value.length === 0) return 'All';
    if (tiers.value.length === 1) return vocabularyTierOptions.find((o) => o.value === tiers.value[0])?.label ?? 'All';
    return `${tiers.value.length} statuses`;
  });

  const activeCount = computed(() => tiers.value.length + (suspended.value === 'show' ? 0 : 1) + (redundant.value === 'show' ? 0 : 1));

  const reset = () => {
    tiers.value = [];
    suspended.value = 'show';
    redundant.value = 'show';
  };

  const toggle = (event: Event) => popover.value.toggle(event);
</script>

<template>
  <div class="relative shrink-0">
    <Button severity="secondary" class="max-md:px-2!" :aria-label="`Display: ${label}`" @click="toggle($event)">
      <Icon name="material-symbols:visibility-outline-rounded" size="1.25em" />
      <span class="hidden md:inline">{{ label }}</span>
    </Button>
    <Badge v-if="activeCount > 0" :value="activeCount" severity="warn" class="absolute -top-2 -right-2 pointer-events-none" />
  </div>

  <Popover ref="popover">
    <div class="flex w-[min(20rem,calc(100vw_-_2rem))] flex-col gap-3 p-1">
      <div class="flex items-center justify-between gap-2">
        <span class="text-sm font-semibold text-gray-700 dark:text-gray-200">Status</span>
        <span class="text-xs text-gray-500 dark:text-gray-400">{{ tiers.length === 0 ? 'All statuses' : `${tiers.length} selected` }}</span>
      </div>

      <div class="flex flex-col gap-1">
        <div v-for="option in vocabularyTierOptions" :key="option.value" class="flex items-center gap-2">
          <Checkbox v-model="tiers" :value="option.value" :input-id="`tier-${option.value}`" class="shrink-0" />
          <label :for="`tier-${option.value}`" class="flex-1 cursor-pointer text-sm">
            {{ option.label }}
            <span class="block text-xs text-gray-500 dark:text-gray-400">{{ option.hint }}</span>
          </label>
        </div>
      </div>

      <div class="border-t border-gray-200 dark:border-gray-700" />

      <div class="flex flex-col gap-1">
        <label class="text-sm">Suspended cards</label>
        <SelectButton v-model="suspended" :options="modeOptions" option-label="label" option-value="value" :allow-empty="false" size="small" class="w-full" />
      </div>

      <div class="flex flex-col gap-1">
        <label class="text-sm">Redundant forms</label>
        <SelectButton v-model="redundant" :options="modeOptions" option-label="label" option-value="value" :allow-empty="false" size="small" class="w-full" />
        <span class="text-xs text-gray-500 dark:text-gray-400">Forms already covered by another form you study</span>
      </div>

      <div class="flex justify-end border-t border-gray-200 dark:border-gray-700 pt-2">
        <Button severity="danger" size="small" :disabled="activeCount === 0" @click="reset">
          <Icon name="material-symbols:refresh" class="mr-1" />
          Reset
        </Button>
      </div>
    </div>
  </Popover>
</template>
