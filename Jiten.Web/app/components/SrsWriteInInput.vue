<script setup lang="ts">
  import { bind, unbind } from 'wanakana';
  import type { WriteInMode } from '~/utils/srsWriteIn';

  const props = defineProps<{
    mode: WriteInMode; // 'reading' | 'meaning' (never 'srs' here)
    placement: 'inline' | 'bar';
    romajiInput: boolean;
    wrongBehavior: 'Reveal' | 'Retry';
    shake: boolean;
    // Transient prompt under the field (e.g. "type a content word") — shown instead of the hint.
    message?: string | null;
    cardKey: string;
    disabled?: boolean;
  }>();

  const emit = defineEmits<{
    submit: [value: string];
    giveUp: [];
  }>();

  const inputRef = ref<HTMLInputElement | null>(null);

  // Reading mode converts romaji → kana live via wanakana's IME binding; meaning mode is plain English.
  const imeBound = computed(() => props.mode === 'reading' && props.romajiInput);

  // wanakana's bind() is idempotent (no-ops if already bound); unbind() THROWS if the element was
  // never bound, so it must be guarded with the attribute check. Wrapped defensively so a wanakana
  // hiccup can never break typing/submitting.
  function applyBinding() {
    const el = inputRef.value;
    if (!el) return;
    try {
      if (imeBound.value) {
        if (!el.hasAttribute('data-wanakana-id')) bind(el, { IMEMode: 'toHiragana' });
      } else if (el.hasAttribute('data-wanakana-id')) {
        unbind(el);
      }
    } catch {
      /* IME binding is a convenience — never let it break the field */
    }
  }

  function focus() {
    // Slight delay so the field is mounted/visible before we grab focus (matches the design's ~30ms).
    setTimeout(() => inputRef.value?.focus(), 30);
  }
  defineExpose({ focus });

  function onSubmit() {
    if (props.disabled) return;
    const value = inputRef.value?.value ?? '';
    if (!value.trim()) return;
    emit('submit', value);
  }

  function onKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter') {
      e.preventDefault();
      onSubmit();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      emit('giveUp');
    }
  }

  // New card: clear the field, refocus.
  watch(
    () => props.cardKey,
    () => {
      if (inputRef.value) inputRef.value.value = '';
      focus();
    }
  );

  watch(imeBound, applyBinding);

  onMounted(() => {
    focus();
    applyBinding();
  });
  onBeforeUnmount(() => {
    const el = inputRef.value;
    if (el?.hasAttribute('data-wanakana-id')) {
      try {
        unbind(el);
      } catch {
        /* already gone */
      }
    }
  });

  // Kept short so it doesn't clip in the narrow inline field; the romaji/kana hint lives below.
  const placeholder = computed(() => (props.mode === 'reading' ? 'Type the reading' : 'Type the meaning'));
  const isBar = computed(() => props.placement === 'bar');
</script>

<template>
  <div class="w-full" :class="isBar ? '' : 'flex flex-col items-center'">
    <div class="relative" :class="[isBar ? 'w-full' : 'w-full max-w-[24rem]', { 'writein-shake': shake }]">
      <input
        ref="inputRef"
        type="text"
        :lang="mode === 'reading' ? 'ja' : 'en'"
        autocomplete="off"
        autocapitalize="off"
        autocorrect="off"
        spellcheck="false"
        :placeholder="placeholder"
        :disabled="disabled"
        :aria-label="mode === 'reading' ? 'Type the reading' : 'Type a meaning'"
        class="w-full text-center rounded-xl border-2 bg-surface-50 dark:bg-surface-800 text-surface-800 dark:text-surface-100 placeholder:text-surface-400 dark:placeholder:text-surface-500 outline-none transition-colors disabled:opacity-60"
        :class="[
          isBar ? 'text-2xl py-4 px-14' : 'text-xl py-3 px-12 font-noto-sans',
          shake
            ? 'border-red-400 bg-red-50 dark:bg-red-950/40'
            : 'border-surface-300 dark:border-surface-600 focus:border-primary-500 focus:bg-surface-0 dark:focus:bg-surface-900 focus:ring-4 focus:ring-primary-500/15',
        ]"
        @keydown="onKeydown"
      />
      <button
        type="button"
        :disabled="disabled"
        class="absolute top-1/2 -translate-y-1/2 right-2 flex items-center justify-center rounded-lg bg-primary-500 hover:bg-primary-600 text-white transition-colors disabled:opacity-60"
        :class="isBar ? 'w-11 h-11' : 'w-9 h-9'"
        aria-label="Check answer"
        @click="onSubmit"
      >
        <Icon name="material-symbols:keyboard-return" :size="isBar ? '22' : '18'" />
      </button>
    </div>

    <div v-if="message" class="mt-2 text-center text-sm text-red-500 dark:text-red-400">{{ message }}</div>
    <div v-else class="mt-2 flex flex-wrap items-center justify-center gap-x-3 gap-y-1 text-xs text-surface-400 dark:text-surface-400">
      <span v-if="mode === 'reading'">Romaji, kana or kanji</span>
      <span class="hidden md:inline">
        Press
        <kbd class="font-sans">Enter</kbd>
        to check
      </span>
    </div>

    <div class="mt-3 flex justify-center">
      <button
        type="button"
        class="inline-flex items-center gap-1.5 rounded-lg border border-surface-300 dark:border-surface-600 bg-surface-0 dark:bg-surface-800 px-3.5 py-2 text-sm font-medium text-surface-600 dark:text-surface-300 shadow-sm hover:border-primary-400 hover:text-primary-600 dark:hover:text-primary-400 hover:bg-primary-50 dark:hover:bg-primary-950/30 active:scale-95 transition cursor-pointer"
        aria-label="Reveal the answer without guessing"
        @click="emit('giveUp')"
      >
        <Icon name="material-symbols:visibility-outline" size="18" />
        Reveal answer
        <span class="hidden md:inline opacity-60">(Esc)</span>
      </button>
    </div>
  </div>
</template>

<style scoped>
  @keyframes writein-shake {
    0%,
    100% {
      transform: translateX(0);
    }
    20% {
      transform: translateX(-7px);
    }
    40% {
      transform: translateX(6px);
    }
    60% {
      transform: translateX(-4px);
    }
    80% {
      transform: translateX(3px);
    }
  }
  .writein-shake {
    animation: writein-shake 0.45s ease;
  }
  @media (prefers-reduced-motion: reduce) {
    .writein-shake {
      animation: none;
    }
  }
</style>
