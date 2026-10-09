<script setup lang="ts">
  import type { MediaGroupTitles } from '~/types';
  import { useFranchiseRename } from '~/composables/useFranchiseRename';

  const props = defineProps<{ franchiseId: number; titles: MediaGroupTitles; nameIsManual: boolean }>();
  const emit = defineEmits<{ saved: [titles: MediaGroupTitles | null]; close: [] }>();

  const rename = useFranchiseRename({
    saved: (titles) => emit('saved', titles),
    close: () => emit('close'),
  });
  const { value, busy, error } = rename;

  rename.reset(props.titles);

  function submit() {
    rename.submit({ franchiseId: props.franchiseId, titles: props.titles, nameIsManual: props.nameIsManual });
  }
</script>

<template>
  <form
    class="flex flex-col gap-2 rounded-md border border-surface-300 p-3 dark:border-surface-700"
    @submit.prevent="submit"
    @keydown.esc.prevent="emit('close')"
  >
    <GroupTitlesFields v-model="value" :invalid="!!error && !value.originalTitle.trim()" autofocus />
    <p class="m-0 text-xs text-surface-600 dark:text-surface-300">
      {{ nameIsManual ? 'Named by an admin. Syncs keep this name.' : 'Named automatically. Every sync renames it until you set a name.' }}
    </p>
    <p v-if="error" class="m-0 text-sm text-red-700 dark:text-red-400" role="alert">{{ error }}</p>
    <div class="flex flex-wrap gap-2">
      <Button label="Save name" type="submit" size="small" :loading="busy" />
      <Button
        v-if="nameIsManual"
        label="Use automatic name"
        type="button"
        severity="secondary"
        size="small"
        :disabled="busy"
        @click="rename.save(franchiseId, null)"
      />
      <Button label="Cancel" type="button" severity="secondary" text size="small" :disabled="busy" @click="emit('close')" />
    </div>
  </form>
</template>
