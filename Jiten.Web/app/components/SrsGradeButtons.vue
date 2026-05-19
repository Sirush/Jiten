<script setup lang="ts">
  import type { IntervalPreviewDto, StudyKeybinds } from '~/types';
  import { FsrsRating } from '~/types';
  import { displayKeyName } from '~/composables/useStudyKeyboard';

  const props = defineProps<{
    gradingButtons: number;
    isFlipped: boolean;
    canUndo: boolean;
    keybinds: StudyKeybinds;
    monochrome?: boolean;
    intervalPreview?: IntervalPreviewDto;
    showKeybinds?: boolean;
    showSwipeHints?: boolean;
    disabled?: boolean;
    pressedKey?: string | null;
    compact?: boolean;
  }>();

  const emit = defineEmits<{
    grade: [rating: FsrsRating];
    flip: [];
    blacklist: [];
    master: [];
    suspend: [];
    bury: [];
    forget: [event: Event];
    undo: [];
    settings: [];
    expand: [];
  }>();

  const morePopover = ref();
  function toggleMore(event: Event) {
    morePopover.value?.toggle(event);
  }

  const buttons4 = computed(() => [
    { rating: FsrsRating.Again, label: 'Again', key: props.keybinds.grade1, severity: 'danger' as const, swipe: '← swipe' },
    { rating: FsrsRating.Hard, label: 'Hard', key: props.keybinds.grade2, severity: 'warn' as const, swipe: null },
    { rating: FsrsRating.Good, label: 'Good', key: props.keybinds.grade3, severity: 'success' as const, swipe: 'swipe →' },
    { rating: FsrsRating.Easy, label: 'Easy', key: props.keybinds.grade4, severity: 'info' as const, swipe: null },
  ]);

  const buttons2 = computed(() => [
    { rating: FsrsRating.Again, label: 'Again', key: props.keybinds.grade1, severity: 'danger' as const, swipe: '← swipe' },
    { rating: FsrsRating.Good, label: 'Good', key: props.keybinds.grade2, severity: 'success' as const, swipe: 'swipe →' },
  ]);

  function formatInterval(seconds: number): string {
    if (seconds < 60) return `${seconds}s`;
    if (seconds < 3600) return `${Math.round(seconds / 60)}m`;
    if (seconds < 86400) return `${Math.round(seconds / 3600)}h`;
    const days = Math.round(seconds / 86400);
    if (days < 30) return `${days}d`;
    if (days < 365) return `${Math.round(days / 30)}mo`;
    return `${(days / 365).toFixed(1)}y`;
  }

  function getIntervalForRating(rating: FsrsRating): string | null {
    if (!props.intervalPreview) return null;
    switch (rating) {
      case FsrsRating.Again: return formatInterval(props.intervalPreview.againSeconds);
      case FsrsRating.Hard: return formatInterval(props.intervalPreview.hardSeconds);
      case FsrsRating.Good: return formatInterval(props.intervalPreview.goodSeconds);
      case FsrsRating.Easy: return formatInterval(props.intervalPreview.easySeconds);
      default: return null;
    }
  }
</script>

<template>
  <div class="flex flex-col w-full mx-auto" :class="compact ? 'gap-1.5' : 'gap-2 sm:gap-3'">
    <!-- Undo (before flip) -->
    <div v-if="!isFlipped && canUndo" class="flex justify-center">
      <Button
        severity="secondary"
        size="small"
        text
        :disabled="props.disabled"
        class="min-h-[36px] !px-2 sm:!px-3"
        :class="{ 'kb-pressed': props.pressedKey === props.keybinds.undo }"
        aria-label="Undo"
        @click="emit('undo')"
      >
        <template #default>
          <div class="flex flex-col items-center sm:flex-row sm:gap-0">
            <Icon name="material-symbols:undo" size="16" class="sm:hidden" />
            <span class="text-[10px] sm:text-sm sm:leading-normal">Undo</span>
            <span v-if="showKeybinds" class="keybind ml-1 text-xs opacity-60 hidden sm:inline">{{ displayKeyName(props.keybinds.undo) }}</span>
          </div>
        </template>
      </Button>
    </div>

    <!-- Flip button when not flipped -->
    <div v-if="!isFlipped" class="flex gap-2 justify-center">
      <Button
        label="Show Answer"
        severity="secondary"
        class="flex-1"
        :class="[{ 'kb-pressed': props.pressedKey === props.keybinds.flipCard }, compact ? 'min-h-[36px] text-base' : 'min-h-[44px] md:min-h-[72px] text-lg']"
        @click="emit('flip')"
      >
        <template #default>
          <span>Show Answer</span>
          <span v-if="showKeybinds && !compact" class="keybind ml-2 text-xs opacity-60">{{ displayKeyName(props.keybinds.flipCard) }}</span>
        </template>
      </Button>
      <button
        v-if="compact"
        class="px-1 rounded text-gray-400 hover:text-gray-600 dark:hover:text-gray-200 transition-colors cursor-pointer"
        aria-label="Expand bar"
        @click="emit('expand')"
      >
        <Icon name="material-symbols:keyboard-double-arrow-up" size="18" />
      </button>
    </div>

    <!-- Grade buttons when flipped -->
    <div v-if="isFlipped" class="flex gap-2 justify-center">
      <Button
        v-for="btn in (gradingButtons === 4 ? buttons4 : buttons2)"
        :key="`${btn.rating}-${props.monochrome}`"
        :severity="props.monochrome ? 'secondary' : btn.severity"
        outlined
        :disabled="props.disabled"
        :aria-label="`Grade: ${btn.label}`"
        class="grade-btn flex-1"
        :class="[{ 'kb-pressed': props.pressedKey === btn.key }, compact ? 'min-h-[36px]' : 'min-h-[44px] md:min-h-[72px]']"
        @click="emit('grade', btn.rating)"
      >
        <template #default>
          <div class="flex flex-col items-center">
            <span v-if="!compact && getIntervalForRating(btn.rating)" class="interval-hint text-[11px] opacity-50">{{ getIntervalForRating(btn.rating) }}</span>
            <span>{{ btn.label }}</span>
            <span v-if="showKeybinds && !compact" class="keybind text-xs opacity-60">{{ displayKeyName(btn.key) }}</span>
            <span v-if="btn.swipe && props.showSwipeHints" class="swipe-hint text-[10px] opacity-50">{{ btn.swipe }}</span>
          </div>
        </template>
      </Button>
      <button
        v-if="compact"
        class="px-1 rounded text-gray-400 hover:text-gray-600 dark:hover:text-gray-200 transition-colors cursor-pointer"
        aria-label="Expand bar"
        @click="emit('expand')"
      >
        <Icon name="material-symbols:keyboard-double-arrow-up" size="18" />
      </button>
    </div>

    <!-- Quick actions -->
    <div v-if="isFlipped && !compact" class="flex gap-1.5 sm:gap-2 justify-center">
      <Button
        severity="secondary"
        size="small"
        outlined
        :disabled="props.disabled"
        class="min-h-[36px] !px-2 sm:!px-3"
        :class="{ 'kb-pressed': props.pressedKey === props.keybinds.blacklist }"
        aria-label="Blacklist"
        @click="emit('blacklist')"
      >
        <template #default>
          <div class="flex flex-col items-center sm:flex-row sm:gap-1.5">
            <Icon name="material-symbols:block" size="16" />
            <span class="text-[10px] sm:text-sm sm:leading-normal">Blacklist</span>
            <span v-if="showKeybinds" class="keybind ml-1 text-xs opacity-60 hidden sm:inline">{{ displayKeyName(props.keybinds.blacklist) }}</span>
          </div>
        </template>
      </Button>
      <!-- Master -->
      <Button
        severity="secondary"
        size="small"
        outlined
        :disabled="props.disabled"
        class="min-h-[36px] !px-2 sm:!px-3"
        :class="{ 'kb-pressed': props.pressedKey === props.keybinds.master }"
        aria-label="Master"
        @click="emit('master')"
      >
        <template #default>
          <div class="flex flex-col items-center sm:flex-row sm:gap-1.5">
            <Icon name="material-symbols:star" size="16" />
            <span class="text-[10px] sm:text-sm sm:leading-normal">Master</span>
            <span v-if="showKeybinds" class="keybind ml-1 text-xs opacity-60 hidden sm:inline">{{ displayKeyName(props.keybinds.master) }}</span>
          </div>
        </template>
      </Button>
      <!-- Undo -->
      <Button
        v-if="canUndo"
        severity="secondary"
        size="small"
        outlined
        :disabled="props.disabled"
        class="min-h-[36px] !px-2 sm:!px-3"
        :class="{ 'kb-pressed': props.pressedKey === props.keybinds.undo }"
        aria-label="Undo"
        @click="emit('undo')"
      >
        <template #default>
          <div class="flex flex-col items-center sm:flex-row sm:gap-1.5">
            <Icon name="material-symbols:undo" size="16" />
            <span class="text-[10px] sm:text-sm sm:leading-normal">Undo</span>
            <span v-if="showKeybinds" class="keybind ml-1 text-xs opacity-60 hidden sm:inline">{{ displayKeyName(props.keybinds.undo) }}</span>
          </div>
        </template>
      </Button>
      <!-- More button -->
      <Button
        severity="secondary"
        size="small"
        outlined
        class="min-h-[36px] !px-2 sm:!px-3"
        aria-label="More actions"
        @click="toggleMore"
      >
        <template #default>
          <div class="flex flex-col items-center sm:flex-row sm:gap-0">
            <Icon name="material-symbols:more-horiz" size="16" class="sm:hidden" />
            <span class="text-[10px] sm:text-sm sm:leading-normal">More</span>
          </div>
        </template>
      </Button>
      <Popover ref="morePopover" :pt="{ content: { class: 'p-1' } }">
        <div class="flex flex-col gap-1 min-w-[140px]">
          <button
            :disabled="props.disabled"
            class="flex items-center gap-2 px-3 py-2 rounded hover:bg-surface-100 dark:hover:bg-surface-800 text-sm w-full text-left disabled:opacity-50"
            @click="emit('bury'); morePopover?.hide()"
          >
            <Icon name="material-symbols:visibility-off-outline" size="16" />
            Bury for a day
          </button>
          <button
            :disabled="props.disabled"
            class="flex items-center gap-2 px-3 py-2 rounded hover:bg-surface-100 dark:hover:bg-surface-800 text-sm w-full text-left disabled:opacity-50"
            @click="emit('suspend'); morePopover?.hide()"
          >
            <Icon name="material-symbols:pause-circle-outline" size="16" />
            Suspend
          </button>
          <button
            :disabled="props.disabled"
            class="flex items-center gap-2 px-3 py-2 rounded hover:bg-surface-100 dark:hover:bg-surface-800 text-sm w-full text-left disabled:opacity-50"
            @click="emit('forget', $event); morePopover?.hide()"
          >
            <Icon name="material-symbols:refresh" size="16" />
            Forget
          </button>
          <button
            class="flex items-center gap-2 px-3 py-2 rounded hover:bg-surface-100 dark:hover:bg-surface-800 text-sm w-full text-left md:hidden"
            @click="emit('settings'); morePopover?.hide()"
          >
            <Icon name="material-symbols:settings-outline" size="16" />
            Settings
          </button>
        </div>
      </Popover>
    </div>
  </div>
</template>

<style scoped>
.grade-btn.p-button {
  font-weight: 900 !important;
  border-width: 3px !important;
}

.kb-pressed.p-button {
  transform: scale(0.93);
  filter: brightness(0.85);
  transition: transform 0.08s ease-out, filter 0.08s ease-out;
}

.keybind {
  display: none;
}

@media (min-width: 768px) {
  .keybind {
    display: inline;
  }
  .swipe-hint {
    display: none;
  }
}
</style>
