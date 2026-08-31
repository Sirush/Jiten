<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import {
    MAX_MEDIA_FILTER_PRESETS,
    MAX_PRESET_NAME_LENGTH,
    type MediaFilterPreset,
    type PresetQuery,
    deletePresetFrom,
    normalisePresetName,
    renamePresetIn,
    savePresetInto,
  } from '~/utils/mediaFilterPresets';

  const props = defineProps<{
    capture: () => PresetQuery;
    activePresetName?: string | null;
  }>();

  const presets = defineModel<MediaFilterPreset[]>('presets', { required: true });
  const defaultName = defineModel<string | null>('defaultName', { required: true });

  const emit = defineEmits<{ apply: [MediaFilterPreset] }>();

  const toast = useToast();

  const listOpen = ref(false);
  const saving = ref(false);
  const draftName = ref('');
  const search = ref('');
  const renaming = ref<string | null>(null);
  const renameDraft = ref('');
  const deleting = ref<string | null>(null);

  const isFull = computed(() => presets.value.length >= MAX_MEDIA_FILTER_PRESETS);
  const trimmedDraft = computed(() => normalisePresetName(draftName.value));
  const overwrites = computed(() => presets.value.some((preset) => preset.name.toLowerCase() === trimmedDraft.value.toLowerCase()));
  const wouldExceed = computed(() => isFull.value && !overwrites.value);

  const visiblePresets = computed(() => {
    const query = search.value.trim().toLowerCase();
    const matching = query ? presets.value.filter((preset) => preset.name.toLowerCase().includes(query)) : presets.value;
    return [...matching].sort((a, b) => a.name.localeCompare(b.name));
  });

  const startSave = () => {
    saving.value = true;
    listOpen.value = true;
    draftName.value = props.activePresetName ?? '';
  };

  const cancelSave = () => {
    saving.value = false;
    draftName.value = '';
  };

  const commitSave = () => {
    if (!trimmedDraft.value || wouldExceed.value) return;
    const result = savePresetInto(presets.value, trimmedDraft.value, props.capture());
    if (result.status === 'full') {
      toast.add({
        severity: 'warn',
        summary: 'Preset limit reached',
        detail: `You can keep up to ${MAX_MEDIA_FILTER_PRESETS} presets. Delete one first.`,
        life: 3500,
      });
      return;
    }
    presets.value = result.presets;
    toast.add({ severity: 'success', summary: result.status === 'replaced' ? `Updated "${trimmedDraft.value}"` : `Saved "${trimmedDraft.value}"`, life: 2000 });
    cancelSave();
  };

  const startRename = (preset: MediaFilterPreset) => {
    deleting.value = null;
    renaming.value = preset.name;
    renameDraft.value = preset.name;
  };

  const commitRename = (preset: MediaFilterPreset) => {
    const result = renamePresetIn(presets.value, preset.name, renameDraft.value);
    if (result.status === 'duplicate') {
      toast.add({ severity: 'warn', summary: 'That name is taken', life: 2500 });
      return;
    }
    if (result.status === 'renamed') {
      if (defaultName.value === preset.name) defaultName.value = normalisePresetName(renameDraft.value);
      presets.value = result.presets;
    }
    renaming.value = null;
  };

  const startDelete = (preset: MediaFilterPreset) => {
    renaming.value = null;
    deleting.value = preset.name;
  };

  const confirmDelete = (preset: MediaFilterPreset) => {
    presets.value = deletePresetFrom(presets.value, preset.name);
    if (defaultName.value === preset.name) defaultName.value = null;
    deleting.value = null;
    toast.add({ severity: 'success', summary: `Deleted "${preset.name}"`, life: 2000 });
  };

  const toggleDefault = (preset: MediaFilterPreset) => {
    defaultName.value = defaultName.value === preset.name ? null : preset.name;
  };
</script>

<template>
  <div class="flex flex-col gap-2">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <div class="flex min-w-0 items-center gap-2">
        <span class="text-sm font-medium text-gray-600 dark:text-gray-300">Presets</span>
        <span v-if="activePresetName" class="min-w-0 truncate text-xs text-surface-500 dark:text-surface-400">{{ activePresetName }} applied</span>
      </div>
      <div class="flex items-center gap-2">
        <Button type="button" size="small" severity="secondary" outlined icon="pi pi-bookmark" label="Save current" @click="startSave" />
        <Button
          type="button"
          size="small"
          severity="secondary"
          outlined
          :icon="listOpen ? 'pi pi-chevron-up' : 'pi pi-chevron-down'"
          :label="`Saved (${presets.length})`"
          :aria-expanded="listOpen"
          @click="listOpen = !listOpen"
        />
      </div>
    </div>

    <div v-if="saving" class="flex flex-wrap items-center gap-2">
      <InputText
        v-model="draftName"
        size="small"
        autofocus
        :maxlength="MAX_PRESET_NAME_LENGTH"
        placeholder="Preset name"
        aria-label="Preset name"
        class="min-w-0 flex-1"
        @keyup.enter="commitSave"
        @keyup.esc="cancelSave"
      />
      <Button type="button" size="small" :label="overwrites ? 'Replace' : 'Save'" :disabled="!trimmedDraft || wouldExceed" @click="commitSave" />
      <Button type="button" size="small" severity="secondary" text label="Cancel" @click="cancelSave" />
      <p v-if="wouldExceed" class="w-full text-xs text-red-500">You have {{ MAX_MEDIA_FILTER_PRESETS }} presets saved. Delete one before saving another.</p>
      <p v-else class="w-full text-xs text-surface-500 dark:text-surface-400">
        Saves every filter, your current sort by and the selected media type. {{ presets.length }} of {{ MAX_MEDIA_FILTER_PRESETS }} saved.
      </p>
    </div>

    <div v-if="listOpen" class="flex flex-col gap-2">
      <IconField v-if="presets.length > 8">
        <InputIcon>
          <Icon name="material-symbols:search-rounded" />
        </InputIcon>
        <InputText v-model="search" size="small" placeholder="Filter presets" aria-label="Filter presets" class="w-full" />
        <InputIcon v-if="search" class="cursor-pointer" @click="search = ''">
          <Icon name="material-symbols:close" />
        </InputIcon>
      </IconField>

      <p v-if="!presets.length" class="text-xs text-surface-500 dark:text-surface-400">
        Nothing saved yet. Set up the filters you want, then choose Save current.
      </p>
      <p v-else-if="!visiblePresets.length" class="text-xs text-surface-500 dark:text-surface-400">No preset matches "{{ search }}".</p>

      <ul v-else class="m-0 flex max-h-56 list-none flex-col gap-0.5 overflow-y-auto p-0">
        <li
          v-for="preset in visiblePresets"
          :key="preset.name"
          class="flex items-center gap-1 rounded-md px-1 py-0.5 hover:bg-surface-100 dark:hover:bg-surface-800"
        >
          <template v-if="renaming === preset.name">
            <InputText
              v-model="renameDraft"
              size="small"
              autofocus
              :maxlength="MAX_PRESET_NAME_LENGTH"
              aria-label="New preset name"
              class="min-w-0 flex-1"
              @keyup.enter="commitRename(preset)"
              @keyup.esc="renaming = null"
            />
            <Button type="button" size="small" text icon="pi pi-check" aria-label="Confirm rename" @click="commitRename(preset)" />
            <Button type="button" size="small" text severity="secondary" icon="pi pi-times" aria-label="Cancel rename" @click="renaming = null" />
          </template>

          <template v-else-if="deleting === preset.name">
            <span class="min-w-0 flex-1 truncate text-sm">Delete "{{ preset.name }}"?</span>
            <Button type="button" size="small" severity="danger" label="Delete" @click="confirmDelete(preset)" />
            <Button type="button" size="small" text severity="secondary" label="Keep" @click="deleting = null" />
          </template>

          <template v-else>
            <button
              type="button"
              class="min-w-0 flex-1 cursor-pointer truncate rounded px-1 py-1 text-left text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-primary-500"
              :class="activePresetName === preset.name ? 'font-medium text-primary-600 dark:text-primary-400' : ''"
              @click="emit('apply', preset)"
            >
              {{ preset.name }}
            </button>
            <Tooltip
              :content="defaultName === preset.name ? 'Stop loading this preset automatically' : 'Load this preset when you open a media list without filters'"
            >
              <Button
                type="button"
                size="small"
                text
                :severity="defaultName === preset.name ? 'warn' : 'secondary'"
                :icon="defaultName === preset.name ? 'pi pi-star-fill' : 'pi pi-star'"
                :label="defaultName === preset.name ? 'Default' : 'Make default'"
                :aria-pressed="defaultName === preset.name"
                class="shrink-0 whitespace-nowrap"
                @click="toggleDefault(preset)"
              />
            </Tooltip>
            <Tooltip content="Rename">
              <Button
                type="button"
                size="small"
                text
                severity="secondary"
                icon="pi pi-pencil"
                :aria-label="`Rename ${preset.name}`"
                @click="startRename(preset)"
              />
            </Tooltip>
            <Button
              type="button"
              size="small"
              text
              severity="secondary"
              icon="pi pi-trash"
              :aria-label="`Delete ${preset.name}`"
              @click="startDelete(preset)"
            />
          </template>
        </li>
      </ul>

      <p v-if="defaultName" class="text-xs text-surface-500 dark:text-surface-400">"{{ defaultName }}" loads when you open a media list without filters.</p>
    </div>
  </div>
</template>
