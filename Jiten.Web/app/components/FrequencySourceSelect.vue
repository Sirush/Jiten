<script setup lang="ts">

  const props = defineProps<{
    inputId?: string;
    label?: string;
    widthClass?: string;
    /** Saved custom lists to offer alongside the media types; each takes the model value -id. */
    lists?: { id: number; name: string }[];
    disabled?: boolean;
  }>();

  // 0 stands for the site-wide ranking; a null value would make Select fall back to its placeholder.
  // A negative value is a custom list id, which keeps the model a single number for every source.
  const model = defineModel<number>({ default: 0 });

  const fieldId = computed(() => props.inputId ?? 'frequencySource');

  const mediaTypeOptions = computed(() =>
    getListedMediaTypes().map((type) => ({
      label: getMediaTypeText(type),
      value: type as number,
    }))
  );

  const options = computed(() => [{ label: 'Global', value: 0 }, ...mediaTypeOptions.value]);

  const groupedOptions = computed(() => [
    { group: 'Site-wide', items: [{ label: 'Global', value: 0 }] },
    { group: 'Media types', items: mediaTypeOptions.value },
    { group: 'Your lists', items: (props.lists ?? []).map((list) => ({ label: list.name, value: -list.id })) },
  ]);

  const useGroups = computed(() => (props.lists?.length ?? 0) > 0);
</script>

<template>
  <FloatLabel variant="on">
    <Select
      v-model="model"
      :input-id="fieldId"
      :options="useGroups ? groupedOptions : options"
      :option-group-label="useGroups ? 'group' : undefined"
      :option-group-children="useGroups ? 'items' : undefined"
      option-label="label"
      option-value="value"
      :disabled="disabled"
      :class="['w-full', widthClass ?? 'md:w-44']"
    />
    <label :for="fieldId">{{ label ?? 'Rank source' }}</label>
  </FloatLabel>
</template>
