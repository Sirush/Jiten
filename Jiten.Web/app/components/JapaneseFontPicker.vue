<script setup lang="ts">
  import { storeToRefs } from 'pinia';
  import { useJitenStore } from '~/stores/jitenStore';
  import { JAPANESE_FONT_OPTIONS } from '~/utils/displaySettingOptions';
  import { isFontFamilyName } from '~/utils/displayProfile';
  import { canListLocalFonts, customFontFamily, hasJapaneseGlyphs, isFontInstalled, listLocalFontFamilies, loadJapaneseFont } from '~/utils/japaneseFonts';

  const { japaneseFont, japaneseCustomFont, japaneseFontWordsOnly, japaneseFontDictionaries } = storeToRefs(useJitenStore());

  onMounted(() => {
    for (const option of JAPANESE_FONT_OPTIONS) loadJapaneseFont(option.value);
  });

  const customDraft = ref(japaneseCustomFont.value);
  watch(japaneseCustomFont, (value) => {
    if (value !== customDraft.value.trim()) customDraft.value = value;
  });
  const draftValid = computed(() => isFontFamilyName(customDraft.value.trim()));
  watch(customDraft, (value) => {
    if (isFontFamilyName(value.trim())) japaneseCustomFont.value = value.trim();
  });

  const installed = ref(true);
  const coversJapanese = ref(true);
  let checkTimer: ReturnType<typeof setTimeout> | undefined;
  const checkInstalled = async () => {
    const name = customDraft.value.trim();
    installed.value = !name || isFontInstalled(name);
    const covers = !name || !installed.value || (await hasJapaneseGlyphs(name));
    if (name === customDraft.value.trim()) coversJapanese.value = covers;
  };
  watch(customDraft, () => {
    clearTimeout(checkTimer);
    checkTimer = setTimeout(checkInstalled, 300);
  });
  onMounted(checkInstalled);
  onBeforeUnmount(() => clearTimeout(checkTimer));

  const localFonts = ref<string[] | null>(null);
  const listingSupported = ref(false);
  const listingFailed = ref(false);
  onMounted(() => (listingSupported.value = canListLocalFonts()));
  const listing = ref(false);
  async function listFonts() {
    listingFailed.value = false;
    listing.value = true;
    try {
      localFonts.value = await listLocalFontFamilies();
    } catch {
      listingFailed.value = true;
    } finally {
      listing.value = false;
    }
  }

  const tileFamily = (option: (typeof JAPANESE_FONT_OPTIONS)[number]) =>
    option.value === 'custom' && customFontFamily(customDraft.value) ? `${customFontFamily(customDraft.value)}, var(--font-noto-sans)` : option.family;

  const tileFontName = (option: (typeof JAPANESE_FONT_OPTIONS)[number]) => (option.value === 'custom' ? customDraft.value.trim() : option.fontName);
</script>

<template>
  <div class="flex flex-col gap-3">
    <div role="radiogroup" aria-label="Japanese font" class="grid grid-cols-2 gap-2 md:grid-cols-3">
      <label
        v-for="option in JAPANESE_FONT_OPTIONS"
        :key="option.value"
        class="font-tile"
        :class="{ 'font-tile--active': japaneseFont === option.value }"
      >
        <input v-model="japaneseFont" type="radio" name="japaneseFont" :value="option.value" class="sr-only" />
        <span
          class="overflow-hidden whitespace-nowrap text-xl leading-tight text-surface-900 dark:text-surface-0 sm:text-2xl"
          lang="ja"
          :style="{ fontFamily: tileFamily(option) }"
          aria-hidden="true"
          >言心令さき</span
        >
        <span class="text-sm font-medium text-surface-900 dark:text-surface-0">{{ option.label }}</span>
        <span class="text-xs text-surface-600 dark:text-surface-400">{{ option.description }}</span>
        <span v-if="tileFontName(option)" class="truncate text-xs italic text-surface-600 dark:text-surface-400">{{ tileFontName(option) }}</span>
      </label>
    </div>

    <div class="flex items-center gap-2">
      <Checkbox v-model="japaneseFontWordsOnly" input-id="japaneseFontWordsOnly" :binary="true" />
      <label for="japaneseFontWordsOnly" class="cursor-pointer text-sm text-surface-900 dark:text-surface-0">Only use this font for words and example sentences</label>
    </div>

    <div class="flex items-center gap-2">
      <Checkbox v-model="japaneseFontDictionaries" input-id="japaneseFontDictionaries" :binary="true" />
      <label for="japaneseFontDictionaries" class="cursor-pointer text-sm text-surface-900 dark:text-surface-0">Also use it for custom dictionaries</label>
    </div>

    <div v-if="japaneseFont === 'custom'" class="flex flex-col gap-2">
      <label for="japaneseCustomFont" class="text-sm font-medium text-surface-900 dark:text-surface-0">Font name</label>
      <div class="flex flex-wrap items-center gap-2">
        <InputText
          id="japaneseCustomFont"
          v-model="customDraft"
          class="min-w-0 flex-1"
          placeholder="For example Meiryo or Hiragino Sans"
          :invalid="!draftValid"
          maxlength="80"
          autocomplete="off"
          spellcheck="false"
        />
        <Button v-if="listingSupported && !localFonts" label="Pick from this device" icon="pi pi-list" severity="secondary" outlined :loading="listing" @click="listFonts" />
      </div>
      <Select
        v-if="localFonts"
        v-model="customDraft"
        :options="localFonts"
        filter
        placeholder="Choose an installed font"
        aria-label="Installed fonts"
        fluid
        :virtual-scroller-options="{ itemSize: 36 }"
      >
        <template #option="{ option }">
          <span :style="{ fontFamily: `&quot;${option}&quot;` }">{{ option }}</span>
          <span class="ml-auto pl-3 text-surface-500" lang="ja" :style="{ fontFamily: `&quot;${option}&quot;` }" aria-hidden="true">言葉</span>
        </template>
      </Select>
      <p v-if="!draftValid" class="text-xs text-red-600 dark:text-red-400">Use letters, numbers, spaces, dots, dashes and underscores only.</p>
      <p v-else-if="!installed" class="text-xs text-amber-700 dark:text-amber-400">
        This device doesn’t have “{{ customDraft.trim() }}”, so Japanese text uses the default font here.
      </p>
      <p v-else-if="!coversJapanese" class="text-xs text-amber-700 dark:text-amber-400">
        “{{ customDraft.trim() }}” has no Japanese characters, so Japanese text uses the default font.
      </p>
      <p v-else-if="listingFailed" class="text-xs text-surface-600 dark:text-surface-400">
        Your browser didn’t share its font list. Type the font’s name instead.
      </p>
      <p class="text-xs text-surface-600 dark:text-surface-400">Devices without this font use the default one.</p>
    </div>
  </div>
</template>

<style scoped>
  .font-tile {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    min-width: 0;
    padding: 0.625rem 0.75rem;
    border: 1px solid var(--p-surface-200);
    border-radius: var(--radius-lg);
    cursor: pointer;
    transition:
      border-color 0.15s,
      background-color 0.15s;
  }

  .font-tile:hover {
    border-color: var(--p-surface-400);
  }

  .font-tile:has(input:focus-visible) {
    outline: 2px solid var(--p-primary-color);
    outline-offset: 2px;
  }

  .font-tile--active,
  .font-tile--active:hover {
    border-color: var(--p-primary-color);
    background: var(--p-primary-50);
  }

  :global(.dark-mode .font-tile) {
    border-color: var(--p-surface-700);
  }

  :global(.dark-mode .font-tile:hover) {
    border-color: var(--p-surface-500);
  }

  :global(.dark-mode .font-tile--active),
  :global(.dark-mode .font-tile--active:hover) {
    border-color: var(--p-primary-400);
    background: color-mix(in srgb, var(--p-primary-950) 40%, transparent);
  }
</style>
