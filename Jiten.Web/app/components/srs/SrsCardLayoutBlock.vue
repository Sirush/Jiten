<script setup lang="ts">
  import Popover from 'primevue/popover';
  import type { CardBlockOptions, CardLayoutBlock, StudySettingsDto } from '~/types';
  import type { LayoutSide } from '~/utils/cardLayout';
  import { cardBlockRegistry } from '~/components/srs/card-blocks/cardBlockRegistry';
  import { blockOptionsSchema } from '~/components/srs/card-blocks/cardBlockOptionsSchema';
  // Explicit import: Nuxt's path-prefixed auto-import would register this as SrsCardBlocksEditorBlockPreview,
  // so the bare <EditorBlockPreview> tag would not resolve.
  import EditorBlockPreview from '~/components/srs/card-blocks/EditorBlockPreview.vue';

  const props = defineProps<{
    block: CardLayoutBlock;
    side: LayoutSide;
    index: number;
    listLength: number;
    settings: StudySettingsDto;
    revealWarning: boolean;
    ghosted: boolean;
  }>();

  const emit = defineEmits<{
    reorderPointerdown: [PointerEvent];
    moveUp: [];
    moveDown: [];
    moveToSide: [];
    duplicate: [];
    remove: [];
    updateOptions: [CardBlockOptions];
  }>();

  const def = computed(() => cardBlockRegistry[props.block.type]);
  const schema = computed(() => blockOptionsSchema[props.block.type] ?? []);

  const resolved = computed<Record<string, unknown>>(() => ({ ...def.value.defaultOptions, ...(props.block.options ?? {}) }));

  const menu = ref<InstanceType<typeof Popover> | null>(null);
  function toggleMenu(e: Event) {
    menu.value?.toggle(e);
  }

  function setOption(key: string, value: unknown) {
    emit('updateOptions', { ...(props.block.options ?? {}), [key]: value } as CardBlockOptions);
  }

  // An empty text field stores no key, matching how the sparse-option model treats a default value.
  function setTextOption(key: string, value: string | null | undefined) {
    const trimmed = (value ?? '').trim();
    setOption(key, trimmed.length ? trimmed : undefined);
  }
</script>

<template>
  <div
    class="rounded-lg border border-surface-200 bg-surface-0 dark:border-surface-700 dark:bg-surface-800/40 transition-opacity"
    :class="{ 'opacity-40': ghosted }"
  >
    <div class="flex items-center gap-2 px-2 py-1.5">
      <button
        type="button"
        class="reorder-handle flex h-7 w-6 shrink-0 cursor-grab touch-none items-center justify-center text-surface-400 hover:text-surface-600 dark:hover:text-surface-200 active:cursor-grabbing"
        aria-label="Drag to reorder"
        title="Drag to reorder"
        @pointerdown="emit('reorderPointerdown', $event)"
      >
        <i class="pi pi-bars text-sm" />
      </button>
      <i :class="def.icon" class="text-sm text-surface-500 dark:text-surface-400" />
      <span class="truncate text-sm font-medium">{{ def.label }}</span>
      <span
        v-if="revealWarning"
        class="inline-flex items-center gap-1 rounded-full bg-amber-100 px-1.5 py-0.5 text-[0.65rem] font-medium text-amber-700 dark:bg-amber-900/40 dark:text-amber-300"
        title="This block reveals the answer while it is on the front of the card."
      >
        <i class="pi pi-eye-slash text-[0.6rem]" />
        Reveals
      </span>
      <div class="ml-auto flex items-center gap-0.5">
        <button
          type="button"
          class="flex h-7 w-7 items-center justify-center rounded-md text-surface-400 hover:bg-surface-100 hover:text-surface-700 dark:hover:bg-surface-700 dark:hover:text-surface-200"
          aria-label="Block options"
          title="Options and move"
          @click="toggleMenu"
        >
          <i class="pi pi-cog text-sm" />
        </button>
        <button
          type="button"
          class="flex h-7 w-7 items-center justify-center rounded-md text-surface-400 hover:bg-surface-100 hover:text-surface-700 dark:hover:bg-surface-700 dark:hover:text-surface-200"
          aria-label="Duplicate block"
          title="Duplicate"
          @click="emit('duplicate')"
        >
          <i class="pi pi-clone text-sm" />
        </button>
        <button
          type="button"
          class="flex h-7 w-7 items-center justify-center rounded-md text-surface-400 hover:bg-red-50 hover:text-red-600 dark:hover:bg-red-900/20 dark:hover:text-red-400"
          aria-label="Remove block"
          title="Remove"
          @click="emit('remove')"
        >
          <i class="pi pi-times text-sm" />
        </button>
      </div>
    </div>

    <div class="pointer-events-none select-none border-t border-surface-100 px-3 py-2 dark:border-surface-700/60" style="zoom: 0.82">
      <EditorBlockPreview :block="block" :side="side" :settings="settings" />
    </div>

    <Popover ref="menu" :pt="{ content: { class: 'p-2' } }">
      <div class="flex w-64 flex-col gap-2">
        <div>
          <p class="mb-1 text-xs font-semibold uppercase tracking-wide text-surface-400">Move</p>
          <div class="grid grid-cols-2 gap-1">
            <button
              type="button"
              class="flex items-center justify-center gap-1.5 rounded-md border border-surface-200 px-2 py-1 text-xs hover:bg-surface-100 disabled:opacity-40 dark:border-surface-700 dark:hover:bg-surface-700"
              :disabled="index === 0"
              @click="emit('moveUp')"
            >
              <i class="pi pi-arrow-up text-[0.65rem]" /> Up
            </button>
            <button
              type="button"
              class="flex items-center justify-center gap-1.5 rounded-md border border-surface-200 px-2 py-1 text-xs hover:bg-surface-100 disabled:opacity-40 dark:border-surface-700 dark:hover:bg-surface-700"
              :disabled="index >= listLength - 1"
              @click="emit('moveDown')"
            >
              <i class="pi pi-arrow-down text-[0.65rem]" /> Down
            </button>
            <button
              type="button"
              class="col-span-2 flex items-center justify-center gap-1.5 rounded-md border border-surface-200 px-2 py-1 text-xs hover:bg-surface-100 dark:border-surface-700 dark:hover:bg-surface-700"
              @click="emit('moveToSide')"
            >
              <i class="pi pi-arrow-right-arrow-left text-[0.65rem]" />
              Move to {{ side === 'front' ? 'back' : 'front' }}
            </button>
          </div>
        </div>

        <div v-if="schema.length" class="border-t border-surface-200 pt-2 dark:border-surface-700">
          <p class="mb-1 text-xs font-semibold uppercase tracking-wide text-surface-400">Options</p>
          <div class="flex flex-col gap-2">
            <div v-for="control in schema" :key="control.key" class="flex items-center justify-between gap-2">
              <label class="shrink-0 text-xs">{{ control.label }}</label>
              <ToggleSwitch
                v-if="control.type === 'toggle'"
                :model-value="!!resolved[control.key]"
                @update:model-value="setOption(control.key, $event)"
              />
              <Select
                v-else-if="control.type === 'select'"
                :model-value="resolved[control.key]"
                :options="control.options"
                option-label="label"
                option-value="value"
                class="min-w-0 flex-1 [&_.p-select-label]:py-1 [&_.p-select-label]:text-xs"
                @update:model-value="setOption(control.key, $event)"
              />
              <div v-else-if="control.type === 'number'" class="flex items-center gap-1">
                <InputNumber
                  :model-value="(resolved[control.key] as number | null)"
                  :min="control.min"
                  :max="control.max"
                  :placeholder="control.placeholder"
                  class="w-24 [&_input]:w-full [&_input]:py-1 [&_input]:text-xs"
                  @update:model-value="setOption(control.key, control.nullable && ($event === null || $event === undefined) ? null : $event)"
                />
                <button
                  v-if="control.nullable && resolved[control.key] != null"
                  type="button"
                  class="text-xs text-surface-400 hover:text-primary-500 dark:text-surface-500 dark:hover:text-primary-400"
                  :title="`Reset to ${control.placeholder ?? 'default'}`"
                  @click.stop="setOption(control.key, null)"
                >
                  <i class="pi pi-times text-[0.65rem]" />
                </button>
              </div>
              <InputText
                v-else-if="control.type === 'text'"
                :model-value="(resolved[control.key] as string) ?? ''"
                :maxlength="control.maxlength"
                :placeholder="control.placeholder"
                class="w-32 min-w-0 py-1 text-xs"
                @update:model-value="setTextOption(control.key, $event)"
              />
            </div>
          </div>
        </div>
      </div>
    </Popover>
  </div>
</template>
