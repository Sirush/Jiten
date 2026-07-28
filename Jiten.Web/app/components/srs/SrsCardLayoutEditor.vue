<script setup lang="ts">
  import type { CardBlockOptions, CardBlockType, CardLayout, CardLayoutBlock, StudySettingsDto } from '~/types';
  import { buildLayoutFromLegacySettings, moveBlock, newBlockId, type LayoutSide } from '~/utils/cardLayout';
  import { cardBlockRegistry } from '~/components/srs/card-blocks/cardBlockRegistry';
  import { useTouchReorderMulti, type ReorderPoint } from '~/composables/useTouchReorderMulti';

  const props = defineProps<{ settings: StudySettingsDto }>();
  // Precedence: a non-null layout is the source of truth; a null one means "still deriving from the
  // legacy toggles". The editor materialises the derived layout into this model on the first edit and
  // never writes null (an explicit clear would be an empty layout, which this editor never sends).
  const model = defineModel<CardLayout | null | undefined>('layout');

  // The frequency rank is card chrome (rendered in the top bar, driven by its own toggle), not a
  // positioned block, so it is hidden from the editor but preserved in the persisted layout.
  const HIDDEN = new Set<CardBlockType>(['frequencyRank']);

  const PALETTE_ORDER: CardBlockType[] = [
    'cardStatus',
    'headword',
    'cardImage',
    'exampleSentence',
    'confusableReadings',
    'etymology',
    'definitions',
    'customMeaning',
    'pitchAccent',
    'kanjiBreakdown',
    'wordComposition',
    'wordUsedIn',
    'deckOccurrences',
    'divider',
  ];
  // Types that can appear more than once, so they stay in the palette even while placed.
  const DUPLICABLE = new Set<CardBlockType>(['divider']);

  const effective = computed<CardLayout>(() => model.value ?? buildLayoutFromLegacySettings(props.settings));
  const frontRows = computed(() => effective.value.front.filter((b) => !HIDDEN.has(b.type)));
  const backRows = computed(() => effective.value.back.filter((b) => !HIDDEN.has(b.type)));
  const rowsFor = (side: LayoutSide) => (side === 'front' ? frontRows : backRows).value;

  const placedTypes = computed(() => new Set([...frontRows.value, ...backRows.value].map((b) => b.type)));
  const paletteTypes = computed(() => PALETTE_ORDER.filter((t) => DUPLICABLE.has(t) || !placedTypes.value.has(t)));

  function newBlock(type: CardBlockType): CardLayoutBlock {
    return { id: newBlockId(), type };
  }

  function cloneBlock(b: CardLayoutBlock): CardLayoutBlock {
    return b.options ? { id: newBlockId(), type: b.type, options: { ...b.options } } : { id: newBlockId(), type: b.type };
  }

  // Materialise-and-commit: reconstructs the full persisted layout from the edited visible rows,
  // re-appending the hidden chrome blocks so they round-trip.
  function commit(front: CardLayoutBlock[], back: CardLayoutBlock[]) {
    const src = effective.value;
    model.value = {
      version: 1,
      front: [...front, ...src.front.filter((b) => HIDDEN.has(b.type))],
      back: [...back, ...src.back.filter((b) => HIDDEN.has(b.type))],
    };
  }

  function editSide(side: LayoutSide, fn: (rows: CardLayoutBlock[]) => void) {
    const front = [...frontRows.value];
    const back = [...backRows.value];
    fn(side === 'front' ? front : back);
    commit(front, back);
  }

  function applyMove(from: { list: LayoutSide; index: number }, to: { list: LayoutSide; index: number }) {
    const { front, back } = moveBlock([...frontRows.value], [...backRows.value], from, to);
    commit(front, back);
  }

  function addType(type: CardBlockType) {
    editSide('front', (rows) => rows.push(newBlock(type)));
  }
  function duplicate(side: LayoutSide, index: number) {
    editSide(side, (rows) => rows.splice(index + 1, 0, cloneBlock(rows[index])));
  }
  function remove(side: LayoutSide, index: number) {
    editSide(side, (rows) => rows.splice(index, 1));
  }
  function updateOptions(side: LayoutSide, index: number, options: CardBlockOptions) {
    editSide(side, (rows) => {
      rows[index] = { ...rows[index], options };
    });
  }
  function moveUp(side: LayoutSide, index: number) {
    if (index > 0) applyMove({ list: side, index }, { list: side, index: index - 1 });
  }
  function moveDown(side: LayoutSide, index: number) {
    if (index < rowsFor(side).length - 1) applyMove({ list: side, index }, { list: side, index: index + 1 });
  }
  function moveToSide(side: LayoutSide, index: number) {
    const other: LayoutSide = side === 'front' ? 'back' : 'front';
    applyMove({ list: side, index }, { list: other, index: rowsFor(other).length });
  }

  function onReorder(from: ReorderPoint, to: ReorderPoint) {
    if (to.list !== 'front' && to.list !== 'back') return;
    if (from.list === 'palette') {
      const type = paletteTypes.value[from.index];
      if (!type) return;
      editSide(to.list, (rows) => rows.splice(to.index, 0, newBlock(type)));
      return;
    }
    if (from.list === to.list && from.index === to.index) return;
    applyMove({ list: from.list as LayoutSide, index: from.index }, { list: to.list, index: to.index });
  }

  const frontEl = ref<HTMLElement | null>(null);
  const backEl = ref<HTMLElement | null>(null);
  const reorder = useTouchReorderMulti({
    getLists: () => [
      { name: 'front', el: frontEl.value },
      { name: 'back', el: backEl.value },
    ],
    onReorder,
  });

  function ghosted(side: LayoutSide, index: number): boolean {
    const f = reorder.fromPoint.value;
    return reorder.isDragging.value && !!f && f.list === side && f.index === index;
  }
  const dropOn = (side: LayoutSide) => reorder.isDragging.value && reorder.dropList.value === side;

  const revealWarning = (block: CardLayoutBlock, side: LayoutSide) => side === 'front' && cardBlockRegistry[block.type].revealsAnswer;
  const frontHasReveal = computed(() => frontRows.value.some((b) => cardBlockRegistry[b.type].revealsAnswer));

  const HINT_KEY = 'srs-layout-reveal-hint-dismissed';
  const hintDismissed = ref(true);
  onMounted(() => {
    try {
      hintDismissed.value = localStorage.getItem(HINT_KEY) === '1';
    } catch {
      hintDismissed.value = false;
    }
  });
  const showRevealHint = computed(() => !hintDismissed.value && frontHasReveal.value);
  function dismissHint() {
    hintDismissed.value = true;
    try {
      localStorage.setItem(HINT_KEY, '1');
    } catch {
      /* private mode — the hint simply reappears next session */
    }
  }
</script>

<template>
  <div class="flex flex-col gap-3">
    <slot name="presetBar" />

    <p class="text-xs text-surface-500 dark:text-surface-400">
      Drag blocks with the handle, or use each block's
      <i class="pi pi-cog text-[0.7rem]" />
      menu to move, duplicate, remove and configure them. The menu bar, reveal hint and grade buttons are fixed.
    </p>

    <!-- FRONT -->
    <section>
      <div class="mb-1.5 flex items-baseline justify-between">
        <h4 class="text-sm font-semibold">Front</h4>
        <span class="text-xs text-surface-400">always visible</span>
      </div>
      <div
        ref="frontEl"
        class="flex min-h-[3rem] flex-col gap-2 rounded-xl border p-2 transition-colors"
        :class="dropOn('front') ? 'border-primary-400 bg-primary-50/40 dark:bg-primary-900/10' : 'border-surface-200 dark:border-surface-700'"
      >
        <SrsCardLayoutBlock
          v-for="(block, i) in frontRows"
          :key="block.id"
          data-reorder-item
          :block="block"
          side="front"
          :index="i"
          :list-length="frontRows.length"
          :settings="settings"
          :reveal-warning="revealWarning(block, 'front')"
          :ghosted="ghosted('front', i)"
          @reorder-pointerdown="reorder.handlePointerDown($event, 'front', i)"
          @move-up="moveUp('front', i)"
          @move-down="moveDown('front', i)"
          @move-to-side="moveToSide('front', i)"
          @duplicate="duplicate('front', i)"
          @remove="remove('front', i)"
          @update-options="updateOptions('front', i, $event)"
        />
        <p v-if="!frontRows.length" class="py-3 text-center text-xs text-surface-400">Front is empty — add or drag a block here.</p>
      </div>
      <Transition name="fade">
        <div
          v-if="showRevealHint"
          class="mt-2 flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-200"
        >
          <i class="pi pi-exclamation-triangle mt-0.5" />
          <span class="flex-1">A block on the front shows the answer before you flip. Intentional? Blocks marked “Reveals” give the answer away.</span>
          <button type="button" class="font-medium underline" @click="dismissHint">Got it</button>
        </div>
      </Transition>
    </section>

    <!-- BACK -->
    <section>
      <div class="mb-1.5 flex items-baseline justify-between">
        <h4 class="text-sm font-semibold">Back</h4>
        <span class="text-xs text-surface-400">shown after flip</span>
      </div>
      <div
        ref="backEl"
        class="flex min-h-[3rem] flex-col gap-2 rounded-xl border p-2 transition-colors"
        :class="dropOn('back') ? 'border-primary-400 bg-primary-50/40 dark:bg-primary-900/10' : 'border-surface-200 dark:border-surface-700'"
      >
        <SrsCardLayoutBlock
          v-for="(block, i) in backRows"
          :key="block.id"
          data-reorder-item
          :block="block"
          side="back"
          :index="i"
          :list-length="backRows.length"
          :settings="settings"
          :reveal-warning="false"
          :ghosted="ghosted('back', i)"
          @reorder-pointerdown="reorder.handlePointerDown($event, 'back', i)"
          @move-up="moveUp('back', i)"
          @move-down="moveDown('back', i)"
          @move-to-side="moveToSide('back', i)"
          @duplicate="duplicate('back', i)"
          @remove="remove('back', i)"
          @update-options="updateOptions('back', i, $event)"
        />
        <p v-if="!backRows.length" class="py-3 text-center text-xs text-surface-400">Back is empty — add or drag a block here.</p>
      </div>
    </section>

    <!-- PALETTE -->
    <section v-if="paletteTypes.length">
      <h4 class="mb-1.5 text-sm font-semibold">Add a block</h4>
      <div class="flex flex-wrap gap-2">
        <button
          v-for="(type, i) in paletteTypes"
          :key="type"
          type="button"
          class="inline-flex touch-none items-center gap-1.5 rounded-full border border-surface-200 bg-surface-0 px-3 py-1.5 text-xs font-medium text-surface-600 hover:border-primary-300 hover:text-primary-600 dark:border-surface-700 dark:bg-surface-800 dark:text-surface-300 dark:hover:text-primary-400"
          :title="`Add ${cardBlockRegistry[type].label} to the front (or drag into a panel)`"
          @pointerdown="reorder.handlePointerDown($event, 'palette', i)"
          @click="!reorder.justDragged.value && addType(type)"
        >
          <i :class="cardBlockRegistry[type].icon" class="text-[0.7rem]" />
          {{ cardBlockRegistry[type].label }}
          <i class="pi pi-plus text-[0.6rem] opacity-60" />
        </button>
      </div>
    </section>
  </div>
</template>

<style scoped>
  .fade-enter-active,
  .fade-leave-active {
    transition: opacity 0.15s ease;
  }
  .fade-enter-from,
  .fade-leave-to {
    opacity: 0;
  }
</style>
