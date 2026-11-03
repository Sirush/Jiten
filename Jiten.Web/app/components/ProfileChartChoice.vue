<script setup lang="ts" generic="T extends string">
  defineProps<{ label: string; options: { label: string; value: T }[] }>();
  const model = defineModel<T>({ required: true });
  const labelId = useId();
</script>

<template>
  <div class="flex flex-col gap-1.5">
    <span :id="labelId" class="text-sm font-semibold">{{ label }}</span>
    <SelectButton
      v-model="model"
      :options="options"
      option-label="label"
      option-value="value"
      :allow-empty="false"
      :aria-labelledby="labelId"
      size="small"
    />
  </div>
</template>
