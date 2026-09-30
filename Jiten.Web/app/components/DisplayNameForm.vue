<script setup lang="ts">
  import type { DisplayNameUpdateResponse } from '~/types/types';

  const props = withDefaults(
    defineProps<{
      initial?: string | null;
      submitLabel: string;
      submitIcon?: string;
      submitIconPos?: 'left' | 'right';
      disabled?: boolean;
      stacked?: boolean;
    }>(),
    { initial: '', submitIcon: 'pi pi-check', submitIconPos: 'left', disabled: false, stacked: false }
  );

  const MAX_LENGTH = 20;

  const emit = defineEmits<{ saved: [result: DisplayNameUpdateResponse] }>();

  const { $api } = useNuxtApp();

  const displayName = ref(props.initial ?? '');
  const loading = ref(false);
  const error = ref<string | null>(null);

  watch(
    () => props.initial,
    (value) => {
      displayName.value = value ?? '';
    }
  );

  const submit = async () => {
    error.value = null;
    const value = displayName.value.trim();
    if (!value) {
      error.value = 'Enter a display name.';
      return;
    }
    loading.value = true;
    try {
      const result = await $api<DisplayNameUpdateResponse>('account/display-name', { method: 'PUT', body: { displayName: value } });
      displayName.value = result.displayName;
      emit('saved', result);
    } catch (err) {
      const e = err as { data?: { message?: string } };
      error.value = e?.data?.message || 'Could not save your display name. Try again.';
    } finally {
      loading.value = false;
    }
  };
</script>

<template>
  <form class="flex flex-col gap-2" @submit.prevent="submit">
    <label for="displayName" class="text-sm font-semibold">Display name</label>
    <div :class="stacked ? 'flex flex-col gap-2' : 'flex flex-col sm:flex-row sm:items-start gap-2'">
      <div :class="stacked ? 'flex flex-col gap-1' : 'flex flex-col gap-1 w-full sm:max-w-xs'">
        <InputText
          id="displayName"
          v-model="displayName"
          :maxlength="MAX_LENGTH"
          autocomplete="nickname"
          :disabled="disabled || loading"
          :invalid="!!error"
          aria-describedby="displayNameHelp"
          :size="stacked ? 'large' : undefined"
          class="w-full"
        />
        <div class="flex justify-between gap-3 text-xs text-muted-color">
          <span id="displayNameHelp">At least 2 characters. Letters, numbers, Japanese, and . _ -</span>
          <span class="tabular-nums shrink-0" aria-hidden="true">{{ displayName.length }}/{{ MAX_LENGTH }}</span>
        </div>
        <small v-if="error" class="text-red-600 dark:text-red-400" role="alert">{{ error }}</small>
      </div>
      <Button
        type="submit"
        :label="submitLabel"
        :icon="submitIcon"
        :icon-pos="submitIconPos"
        :loading="loading"
        :disabled="disabled"
        :size="stacked ? 'large' : undefined"
        :class="stacked ? 'w-full mt-2' : 'w-full sm:w-auto shrink-0'"
      />
    </div>
  </form>
</template>
