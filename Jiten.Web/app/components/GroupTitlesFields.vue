<script setup lang="ts">
  import InputText from 'primevue/inputtext';
  import { GROUP_TITLE_MAX_LENGTH, type GroupTitlesDraft } from '~/utils/groupTitles';

  const props = defineProps<{ invalid?: boolean; autofocus?: boolean }>();
  const model = defineModel<GroupTitlesDraft>({ required: true });

  const baseId = useId();
  const originalInput = ref<{ $el?: HTMLElement } | null>(null);

  const fields = [
    { key: 'romajiTitle', label: 'Romaji title' },
    { key: 'englishTitle', label: 'English title' },
  ] as const;

  function update(key: keyof GroupTitlesDraft, value: string | undefined) {
    model.value = { ...model.value, [key]: value ?? '' };
  }

  function focus() {
    const el = originalInput.value?.$el;
    const field = el instanceof HTMLInputElement ? el : el?.querySelector('input');
    field?.focus();
    field?.select();
  }

  onMounted(async () => {
    if (!props.autofocus) return;
    await nextTick();
    focus();
  });

  defineExpose({ focus });
</script>

<template>
  <div class="flex flex-col gap-2">
    <div class="flex flex-col gap-1">
      <label :for="`${baseId}-original`" class="text-sm font-medium">Original title</label>
      <InputText
        :id="`${baseId}-original`"
        ref="originalInput"
        :model-value="model.originalTitle"
        class="w-full"
        :maxlength="GROUP_TITLE_MAX_LENGTH"
        :invalid="invalid"
        aria-required="true"
        @update:model-value="(v) => update('originalTitle', v)"
      />
    </div>
    <div class="grid grid-cols-1 gap-2 sm:grid-cols-2">
      <div v-for="f in fields" :key="f.key" class="flex min-w-0 flex-col gap-1">
        <label :for="`${baseId}-${f.key}`" class="text-sm font-medium">
          {{ f.label }} <span class="font-normal text-surface-600 dark:text-surface-300">(optional)</span>
        </label>
        <InputText
          :id="`${baseId}-${f.key}`"
          :model-value="model[f.key]"
          class="w-full"
          :maxlength="GROUP_TITLE_MAX_LENGTH"
          @update:model-value="(v) => update(f.key, v)"
        />
      </div>
    </div>
  </div>
</template>
