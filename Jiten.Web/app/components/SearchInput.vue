<script setup lang="ts">
  withDefaults(
    defineProps<{
      placeholder?: string;
      ariaLabel?: string;
      size?: 'small';
      searchIconClass?: string;
      clearIconClass?: string;
    }>(),
    { placeholder: 'Search' }
  );

  const model = defineModel<string>({ required: true });

  const emit = defineEmits<{
    compositionstart: [event: CompositionEvent];
    compositionend: [event: CompositionEvent];
  }>();

  const input = ref();
  const clear = () => {
    model.value = '';
    nextTick(() => input.value?.$el?.focus());
  };
</script>

<template>
  <IconField>
    <InputIcon :class="searchIconClass">
      <Icon v-if="!searchIconClass" name="material-symbols:search-rounded" />
    </InputIcon>
    <InputText
      ref="input"
      v-model="model"
      type="text"
      :placeholder="placeholder"
      :aria-label="ariaLabel"
      class="w-full"
      :size="size"
      @compositionstart="emit('compositionstart', $event)"
      @compositionend="emit('compositionend', $event)"
    />
    <InputIcon v-if="model" :class="[clearIconClass, 'cursor-pointer']" @click="clear">
      <Icon v-if="!clearIconClass" name="material-symbols:close" />
    </InputIcon>
  </IconField>
</template>
