<script setup lang="ts">
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import type { CardLayout, CardLayoutPreset, StudySettingsDto } from '~/types';
  import { buildLayoutFromLegacySettings } from '~/utils/cardLayout';
  import {
    BUILT_IN_PRESETS,
    decodeLayoutShareCode,
    encodeLayoutShareCode,
    instantiatePreset,
    type DecodedShareLayout,
  } from '~/utils/cardLayoutPresets';
  import { cardBlockRegistry } from '~/components/srs/card-blocks/cardBlockRegistry';

  const props = defineProps<{ settings: StudySettingsDto }>();
  const layout = defineModel<CardLayout | null | undefined>('layout');
  const presets = defineModel<CardLayoutPreset[] | undefined>('presets');

  const confirm = useConfirm();
  const toast = useToast();

  const MAX_PRESETS = 10;
  const MAX_NAME = 40;

  const effectiveLayout = computed<CardLayout>(() => layout.value ?? buildLayoutFromLegacySettings(props.settings));
  const userPresets = computed<CardLayoutPreset[]>(() => presets.value ?? []);

  interface PresetOption {
    label: string;
    kind: 'builtin' | 'user';
    layout: CardLayout;
  }
  const presetGroups = computed(() => {
    const groups: { label: string; items: PresetOption[] }[] = [
      { label: 'Built-in', items: BUILT_IN_PRESETS.map((p) => ({ label: p.name, kind: 'builtin' as const, layout: p.layout })) },
    ];
    if (userPresets.value.length) {
      groups.push({
        label: 'Your presets',
        items: userPresets.value.map((p) => ({ label: p.name, kind: 'user' as const, layout: p.layout })),
      });
    }
    return groups;
  });

  // A layout is "unsaved" when it matches neither a built-in nor a saved preset. The share code strips
  // ids and default options, so equal codes mean structurally identical layouts.
  const currentCode = computed(() => encodeLayoutShareCode(effectiveLayout.value));
  const savedCodes = computed(() => new Set([...BUILT_IN_PRESETS, ...userPresets.value].map((p) => encodeLayoutShareCode(p.layout))));
  const hasUnsavedLayout = computed(() => !savedCodes.value.has(currentCode.value));

  const selectedPreset = ref<PresetOption | null>(null);

  function applyLayout(next: CardLayout) {
    layout.value = next;
  }

  function onSelectPreset(option: PresetOption | null) {
    if (!option) return;
    const apply = () => {
      applyLayout(instantiatePreset(option.layout));
      toast.add({ severity: 'success', summary: `Applied "${option.label}"`, life: 2000 });
    };
    if (hasUnsavedLayout.value) {
      confirm.require({
        header: 'Apply preset',
        message: 'Your current layout is not saved as a preset and will be replaced. Continue?',
        icon: 'pi pi-exclamation-triangle',
        rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
        acceptProps: { label: 'Apply' },
        accept: apply,
      });
    } else {
      apply();
    }
    nextTick(() => {
      selectedPreset.value = null;
    });
  }

  function requestDeletePreset(name: string, event: Event) {
    confirm.require({
      target: event.currentTarget as HTMLElement,
      message: `Delete the "${name}" preset?`,
      icon: 'pi pi-trash',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Delete', severity: 'danger' },
      accept: () => {
        presets.value = userPresets.value.filter((p) => p.name !== name);
        toast.add({ severity: 'success', summary: `Deleted "${name}"`, life: 2000 });
      },
    });
  }

  const resetToDefault = () => {
    const def = BUILT_IN_PRESETS.find((p) => p.name === 'Default')!;
    const apply = () => {
      applyLayout(instantiatePreset(def.layout));
      toast.add({ severity: 'success', summary: 'Reset to default layout', life: 2000 });
    };
    if (hasUnsavedLayout.value) {
      confirm.require({
        header: 'Reset layout',
        message: 'Reset the card layout to the default? Your current unsaved layout will be lost.',
        icon: 'pi pi-exclamation-triangle',
        rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
        acceptProps: { label: 'Reset', severity: 'danger' },
        accept: apply,
      });
    } else {
      apply();
    }
  };

  const shareDialog = ref(false);
  const shareCode = ref('');
  const shareTextarea = ref<{ $el?: HTMLTextAreaElement } | null>(null);
  function openShareDialog() {
    shareCode.value = encodeLayoutShareCode(effectiveLayout.value);
    shareDialog.value = true;
  }
  function selectShareCode() {
    nextTick(() => {
      const el = shareTextarea.value?.$el;
      el?.focus();
      el?.select();
    });
  }
  async function copyShareCode() {
    try {
      await navigator.clipboard.writeText(shareCode.value);
      toast.add({ severity: 'success', summary: 'Share code copied', detail: 'Paste it anywhere to share this layout.', life: 2500 });
    } catch {
      selectShareCode();
      toast.add({ severity: 'warn', summary: 'Could not copy automatically', detail: 'The code is selected — copy it manually.', life: 3500 });
    }
  }

  const saveDialog = ref(false);
  const saveName = ref('');
  function openSaveDialog() {
    saveName.value = '';
    saveDialog.value = true;
  }
  function commitSave(name: string, list: CardLayoutPreset[], index: number) {
    const snapshot = instantiatePreset(effectiveLayout.value);
    const next = [...list];
    if (index >= 0) next[index] = { name, layout: snapshot };
    else next.push({ name, layout: snapshot });
    presets.value = next;
    saveDialog.value = false;
    toast.add({ severity: 'success', summary: `Saved "${name}"`, life: 2000 });
  }
  function savePreset() {
    const name = saveName.value.trim();
    if (!name) return;
    const list = userPresets.value;
    const index = list.findIndex((p) => p.name === name);
    if (index >= 0) {
      confirm.require({
        header: 'Overwrite preset',
        message: `A preset named "${name}" already exists. Overwrite it?`,
        icon: 'pi pi-exclamation-triangle',
        rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
        acceptProps: { label: 'Overwrite' },
        accept: () => commitSave(name, list, index),
      });
      return;
    }
    if (list.length >= MAX_PRESETS) {
      toast.add({ severity: 'warn', summary: 'Preset limit reached', detail: `You can keep up to ${MAX_PRESETS} presets. Delete one first.`, life: 3500 });
      return;
    }
    commitSave(name, list, -1);
  }

  const importDialog = ref(false);
  const importCode = ref('');
  const importResult = computed<DecodedShareLayout | null>(() => (importCode.value.trim() ? decodeLayoutShareCode(importCode.value) : null));
  const importInvalid = computed(() => !!importCode.value.trim() && !importResult.value);
  function openImportDialog() {
    importCode.value = '';
    importDialog.value = true;
  }
  function applyImport() {
    if (!importResult.value) return;
    applyLayout(importResult.value.layout);
    importDialog.value = false;
    toast.add({ severity: 'success', summary: 'Layout imported', life: 2000 });
  }
  const chipLabel = (type: string) => cardBlockRegistry[type as keyof typeof cardBlockRegistry]?.label ?? type;
  const chipIcon = (type: string) => cardBlockRegistry[type as keyof typeof cardBlockRegistry]?.icon ?? 'pi pi-question';
</script>

<template>
  <div class="flex flex-col gap-2">
    <div class="flex flex-wrap items-center gap-2">
      <Select
        v-model="selectedPreset"
        :options="presetGroups"
        option-label="label"
        option-group-label="label"
        option-group-children="items"
        placeholder="Load a preset…"
        class="w-full sm:w-56"
        @change="onSelectPreset($event.value)"
      >
        <template #optiongroup="{ option }">
          <div class="px-1 py-0.5 text-xs font-semibold text-surface-500 dark:text-surface-400">{{ option.label }}</div>
        </template>
        <template #option="{ option }">
          <div class="flex w-full items-center justify-between gap-2">
            <span class="truncate">{{ option.label }}</span>
            <button
              v-if="option.kind === 'user'"
              type="button"
              class="shrink-0 rounded p-1 text-surface-400 hover:bg-surface-100 hover:text-red-500 dark:hover:bg-surface-700"
              title="Delete preset"
              @click.stop="requestDeletePreset(option.label, $event)"
            >
              <i class="pi pi-trash text-xs" />
            </button>
          </div>
        </template>
      </Select>

      <Button type="button" size="small" severity="secondary" outlined icon="pi pi-save" label="Save as…" @click="openSaveDialog" />
      <Button type="button" size="small" severity="secondary" outlined icon="pi pi-replay" label="Reset" @click="resetToDefault" />
      <Button type="button" size="small" severity="secondary" outlined icon="pi pi-share-alt" label="Share" @click="openShareDialog" />
      <Button type="button" size="small" severity="secondary" outlined icon="pi pi-download" label="Import" @click="openImportDialog" />
    </div>

    <Dialog v-model:visible="saveDialog" modal header="Save layout preset" :style="{ width: '22rem' }" :dismissable-mask="true">
      <div class="flex flex-col gap-3">
        <label class="text-sm text-surface-600 dark:text-surface-300" for="preset-name">Preset name</label>
        <InputText id="preset-name" v-model="saveName" :maxlength="MAX_NAME" autofocus placeholder="e.g. My reading deck" @keyup.enter="savePreset" />
        <p class="text-xs text-surface-400">{{ userPresets.length }} / {{ MAX_PRESETS }} presets saved.</p>
      </div>
      <template #footer>
        <Button type="button" size="small" severity="secondary" outlined label="Cancel" @click="saveDialog = false" />
        <Button type="button" size="small" label="Save" :disabled="!saveName.trim()" @click="savePreset" />
      </template>
    </Dialog>

    <Dialog v-model:visible="shareDialog" modal header="Share layout" :style="{ width: '28rem' }" :dismissable-mask="true" @show="selectShareCode">
      <div class="flex flex-col gap-3">
        <label class="text-sm text-surface-600 dark:text-surface-300" for="share-code">Copy this code and paste it anywhere to share your layout.</label>
        <Textarea id="share-code" ref="shareTextarea" :model-value="shareCode" readonly rows="3" auto-resize class="w-full font-mono text-xs" @focus="selectShareCode" />
      </div>
      <template #footer>
        <Button type="button" size="small" severity="secondary" outlined label="Close" @click="shareDialog = false" />
        <Button type="button" size="small" icon="pi pi-copy" label="Copy" @click="copyShareCode" />
      </template>
    </Dialog>

    <Dialog v-model:visible="importDialog" modal header="Import layout" :style="{ width: '28rem' }" :dismissable-mask="true">
      <div class="flex flex-col gap-3">
        <label class="text-sm text-surface-600 dark:text-surface-300" for="import-code">Paste a share code</label>
        <Textarea id="import-code" v-model="importCode" rows="3" auto-resize class="w-full font-mono text-xs" placeholder="jitenlayout1.…" />
        <p v-if="importInvalid" class="text-xs text-red-500">That share code could not be read. Check you copied all of it.</p>

        <div v-if="importResult" class="flex flex-col gap-2">
          <div v-if="importResult.droppedTypes.length" class="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-200">
            <i class="pi pi-info-circle mr-1" />
            Some blocks from a newer version were skipped: {{ importResult.droppedTypes.join(', ') }}.
          </div>
          <div>
            <p class="mb-1 text-xs font-semibold text-surface-500 dark:text-surface-400">Front</p>
            <div class="flex flex-wrap gap-1.5">
              <span
                v-for="(b, i) in importResult.layout.front"
                :key="`f${i}`"
                class="inline-flex items-center gap-1 rounded-full border border-surface-200 bg-surface-0 px-2 py-0.5 text-xs text-surface-600 dark:border-surface-700 dark:bg-surface-800 dark:text-surface-300"
              >
                <i :class="chipIcon(b.type)" class="text-[0.65rem]" />{{ chipLabel(b.type) }}
              </span>
              <span v-if="!importResult.layout.front.length" class="text-xs text-surface-400">empty</span>
            </div>
          </div>
          <div>
            <p class="mb-1 text-xs font-semibold text-surface-500 dark:text-surface-400">Back</p>
            <div class="flex flex-wrap gap-1.5">
              <span
                v-for="(b, i) in importResult.layout.back"
                :key="`b${i}`"
                class="inline-flex items-center gap-1 rounded-full border border-surface-200 bg-surface-0 px-2 py-0.5 text-xs text-surface-600 dark:border-surface-700 dark:bg-surface-800 dark:text-surface-300"
              >
                <i :class="chipIcon(b.type)" class="text-[0.65rem]" />{{ chipLabel(b.type) }}
              </span>
              <span v-if="!importResult.layout.back.length" class="text-xs text-surface-400">empty</span>
            </div>
          </div>
        </div>
      </div>
      <template #footer>
        <Button type="button" size="small" severity="secondary" outlined label="Cancel" @click="importDialog = false" />
        <Button type="button" size="small" label="Apply" :disabled="!importResult" @click="applyImport" />
      </template>
    </Dialog>
  </div>
</template>
