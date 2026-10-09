<script setup lang="ts">
  import InputText from 'primevue/inputtext';
  import { FRANCHISE_NAME_MAX_LENGTH, useFranchiseRename } from '~/composables/useFranchiseRename';

  const props = defineProps<{ franchiseId: number; name: string; nameIsManual: boolean }>();
  const emit = defineEmits<{ saved: [name: string | null]; close: [] }>();

  const rename = useFranchiseRename({
    saved: (name) => emit('saved', name),
    close: () => emit('close'),
  });
  const { value, busy, error } = rename;
  const input = ref<{ $el?: HTMLElement } | null>(null);
  const inputId = useId();

  onMounted(async () => {
    rename.reset(props.name);
    await nextTick();
    const el = input.value?.$el;
    const field = el instanceof HTMLInputElement ? el : el?.querySelector('input');
    field?.focus();
    field?.select();
  });

  function submit() {
    rename.submit({ franchiseId: props.franchiseId, name: props.name, nameIsManual: props.nameIsManual });
  }
</script>

<template>
  <form class="flex flex-col gap-2 rounded-md border border-surface-300 p-3 dark:border-surface-700" @submit.prevent="submit" @keydown.esc.prevent="emit('close')">
    <label :for="inputId" class="text-sm font-medium">Franchise name</label>
    <InputText :id="inputId" ref="input" v-model="value" class="w-full" :maxlength="FRANCHISE_NAME_MAX_LENGTH" :invalid="!!error" />
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
