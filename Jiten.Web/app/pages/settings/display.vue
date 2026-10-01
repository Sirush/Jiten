<script setup lang="ts">
  import { storeToRefs } from 'pinia';
  import { useConfirm } from 'primevue/useconfirm';
  import { DEFAULT_DICTIONARY_FONT_SIZE, useJitenStore } from '~/stores/jitenStore';
  import { useDisplayStyleStore } from '~/stores/displayStyleStore';
  import { useAuthStore } from '~/stores/authStore';
  import { useDisplayProfileStore } from '~/stores/displayProfileStore';
  import { kanjiScaleMembership, kanjiScaleOptions } from '~/data/kanjiGroupings';
  import {
    DIFFICULTY_DISPLAY_STYLE_OPTIONS,
    DIFFICULTY_PALETTE_OPTIONS,
    DIFFICULTY_VALUE_DISPLAY_STYLE_OPTIONS,
    HEADWORD_FURIGANA_OPTIONS,
    LIST_VIEW_OPTIONS,
    PITCH_ACCENT_OPTIONS,
    REDUCED_MOTION_OPTIONS,
    SENTENCE_FURIGANA_OPTIONS,
    TEXT_SIZE_OPTIONS,
    THEME_OPTIONS,
    TITLE_LANGUAGE_OPTIONS,
    TTS_VOICE_OPTIONS,
  } from '~/utils/displaySettingOptions';
  import { localiseTitleWithLanguage } from '~/utils/localiseTitle';
  import { DEFAULT_TTS_VOLUME, resolveTtsVolume } from '~/utils/ttsVolume';
  import { DEFAULT_WORD_STATE_COLOURS, WORD_STATE_COLOUR_KEYS, WORD_STATE_COLOUR_LABELS, wordColourStyle, type WordStateColourKey } from '~/utils/wordState';
  import { DEFAULT_READING_SPEEDS, MAX_READING_SPEED, MIN_READING_SPEED } from '~/utils/displayProfile';
  import { AVERAGE_BAND, difficultyNames } from '~/utils/difficultyColours';
  import { PITCH_CATEGORY_LABELS, type PitchCategory } from '~/utils/pitchAccent';

  useHead({ title: 'Display - Settings' });
  useSeoMeta({ robots: 'noindex' });

  const store = useJitenStore();
  const {
    titleLanguage,
    themeMode,
    headwordFurigana,
    sentenceFurigana,
    headwordSize,
    sentenceSize,
    furiganaSize,
    furiganaOnHover,
    pitchAccentDisplay,
    pitchAccentColours,
    colourWordsByState,
    reducedMotion,
    readingSpeed,
    readingSpeedByDifficulty,
    readingSpeeds,
    difficultyPalette,
    displayAllNsfw,
    hideVocabularyDefinitions,
    quickMasterVocabulary,
    ttsVoice,
    difficultyDisplayStyle,
    difficultyValueDisplayStyle,
    kanjiScale,
  } = storeToRefs(store);
  const { displayStyle } = storeToRefs(useDisplayStyleStore());
  const auth = useAuthStore();
  const profiles = useDisplayProfileStore();
  const confirm = useConfirm();
  const { speakWord, isSpeaking, isLoading: isTtsLoading } = useTts(undefined, 'word');

  const search = ref('');
  const anyMatch = provideSettingsSearch(search);

  const colours = computed(() => store.resolvedStateColours);
  const setColour = (key: WordStateColourKey, value: string | null) => {
    store.stateColours = { ...colours.value, [key]: value };
  };
  const highlightedColour = ref<WordStateColourKey | null>(null);
  const systemDark = ref(false);
  onMounted(() => {
    systemDark.value = window.matchMedia('(prefers-color-scheme: dark)').matches;
  });
  const isDark = computed(() => themeMode.value === 'dark' || (themeMode.value === 'auto' && systemDark.value));
  // Native colour inputs need a concrete value; an unset colour shows the theme's text colour.
  const colourInputValue = (key: WordStateColourKey) => colours.value[key] ?? (isDark.value ? '#f3f4f6' : '#111827');

  const TITLE_EXAMPLES = [
    { originalTitle: '進撃の巨人', romajiTitle: 'Shingeki no Kyojin', englishTitle: 'Attack on Titan' },
    { originalTitle: 'サクラノ詩', romajiTitle: 'Sakura no Uta', englishTitle: null },
  ];
  const titleExamples = computed(() => TITLE_EXAMPLES.map((deck) => localiseTitleWithLanguage(deck, titleLanguage.value)));

  // One media title per band of the scale, so every colour and label shows.
  const DIFFICULTY_EXAMPLES = [0.5, 1.5, 2.5, 3.5, 4.5, 5];

  const NOVEL_CHARACTERS = 120_000;
  const novelHours = computed(() => (readingSpeed.value ? Math.round(NOVEL_CHARACTERS / readingSpeed.value) : 0));
  const bandSpeed = (band: number) => (readingSpeedByDifficulty.value ? readingSpeeds.value[band]! : readingSpeed.value);
  const bandNovelHours = (band: number) => Math.max(1, Math.round(NOVEL_CHARACTERS / (bandSpeed(band) || 1)));

  const { textClass: difficultyTextClass } = useDifficultyColours();
  const clampSpeed = (value: number) => Math.min(MAX_READING_SPEED, Math.max(MIN_READING_SPEED, Math.round(value)));
  const useOneSpeed = (speed = readingSpeedByDifficulty.value ? readingSpeeds.value[AVERAGE_BAND]! : readingSpeed.value) => {
    readingSpeed.value = speed;
    readingSpeeds.value = [...DEFAULT_READING_SPEEDS];
    readingSpeedByDifficulty.value = false;
  };
  // Per-difficulty speeds are on exactly when the bands differ; otherwise they'd change nothing.
  const saveBandSpeeds = (next: number[]) => {
    if (next.every((speed) => speed === next[0])) return useOneSpeed(next[0]);
    readingSpeeds.value = next;
    readingSpeedByDifficulty.value = true;
  };
  const setBandSpeed = (band: number, value: number | null) => {
    if (value == null) return;
    const next = DEFAULT_READING_SPEEDS.map((_, i) => bandSpeed(i));
    next[band] = clampSpeed(value);
    saveBandSpeeds(next);
  };
  const SUGGESTED_SPEED_FACTORS = [1.2, 1.1, 1, 0.85, 0.7, 0.5];
  const suggestBandSpeeds = () => {
    saveBandSpeeds(SUGGESTED_SPEED_FACTORS.map((factor) => clampSpeed(Math.round((readingSpeed.value * factor) / 100) * 100)));
  };
  const bandSpeedsOpen = ref(readingSpeedByDifficulty.value);
  watch(readingSpeedByDifficulty, (on) => {
    if (on) bandSpeedsOpen.value = true;
  });

  const PITCH_CATEGORIES = Object.keys(PITCH_CATEGORY_LABELS) as PitchCategory[];

  const KANJI_EXAMPLES = [
    { character: '日', grade: 1, strokes: 4, meaning: 'day, sun' },
    { character: '語', grade: 2, strokes: 14, meaning: 'word, language' },
    { character: '憂', grade: 8, strokes: 15, meaning: 'melancholy' },
  ];
  const kanjiBadge = (kanji: (typeof KANJI_EXAMPLES)[number]) =>
    kanjiScale.value === 'none' ? null : kanjiScaleMembership(kanji.character, kanjiScale.value, kanji.grade);

  const deviceIsDefault = computed(
    () => resolveTtsVolume(store.ttsVolume) === DEFAULT_TTS_VOLUME && store.customDictionaryFontSize === DEFAULT_DICTIONARY_FONT_SIZE
  );
  const resetDevice = () => {
    store.ttsVolume = DEFAULT_TTS_VOLUME;
    store.customDictionaryFontSize = DEFAULT_DICTIONARY_FONT_SIZE;
  };

  const confirmReset = () => {
    confirm.require({
      header: 'Restore Jiten’s defaults?',
      message: auth.isAuthenticated
        ? `Every setting on this page goes back to its default in “${profiles.activeProfile?.name ?? 'this profile'}”. Settings under This device are kept.`
        : 'Every setting on this page goes back to its default. Settings under This device are kept.',
      icon: 'pi pi-undo',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Restore defaults' },
      accept: () => profiles.resetToDefaults(),
    });
  };
</script>

<template>
  <div class="display-settings container mx-auto flex flex-col gap-4 p-2 md:p-4">
    <div class="flex flex-wrap items-center gap-2">
      <NuxtLink v-if="auth.isAuthenticated" to="/settings" aria-label="Back to settings" class="rounded-full">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded tabindex="-1" />
      </NuxtLink>
      <div class="min-w-0 flex-1">
        <h1 class="text-2xl font-bold">Display</h1>
        <p class="text-sm text-surface-600 dark:text-surface-400">Customise Jiten's appearance</p>
      </div>
      <Button label="Restore defaults" icon="pi pi-undo" size="small" severity="secondary" outlined @click="confirmReset" />
    </div>

    <div class="display-search sticky top-0 z-10 -mx-2 px-2 py-2 md:-mx-4 md:px-4">
      <IconField>
        <InputIcon class="pi pi-search" />
        <InputText v-model="search" type="search" placeholder="Find a setting" aria-label="Find a setting" class="w-full" />
      </IconField>
    </div>

    <div v-if="!anyMatch" role="status" class="flex flex-col items-start gap-2 rounded-lg border border-dashed border-surface-300 p-4 text-sm dark:border-surface-600">
      <span>No display setting matches “{{ search.trim() }}”.</span>
      <Button label="Clear the search" size="small" severity="secondary" outlined @click="search = ''" />
    </div>

    <DisplaySection
      icon="pi pi-id-card"
      title="Profiles"
      description="Save presets that you can quickly switch between or use on different devices."
      keywords="profile device switch save rename delete"
    >
      <DisplayProfileBar />
    </DisplaySection>

    <DisplaySection
      icon="pi pi-language"
      title="Japanese text"
      description="Furigana, font and example sentences."
      :resettable="!profiles.isSectionDefault('japaneseText')"
      @reset="profiles.resetSection('japaneseText')"
    >
      <div class="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,20rem)_minmax(0,1fr)]">
        <div class="lg:sticky lg:top-16 lg:self-start">
          <JapaneseTextPreview />
        </div>
        <div class="grid grid-cols-1 gap-x-6 gap-y-5 sm:grid-cols-2">
          <DisplaySettingRow label="Furigana on words" for="headwordFurigana" description="Word pages and vocabulary lists." keywords="reading ruby kana">
            <Select
              v-model="headwordFurigana"
              :options="HEADWORD_FURIGANA_OPTIONS"
              option-label="label"
              option-value="value"
              input-id="headwordFurigana"
              fluid
            />
          </DisplaySettingRow>
          <DisplaySettingRow
            label="Furigana in example sentences"
            for="sentenceFurigana"
            description="Example sentences everywhere on the site."
            keywords="reading ruby kana"
          >
            <Select
              v-model="sentenceFurigana"
              :options="SENTENCE_FURIGANA_OPTIONS"
              option-label="label"
              option-value="value"
              input-id="sentenceFurigana"
              fluid
            />
          </DisplaySettingRow>

          <DisplaySettingRow label="Furigana size" keywords="reading ruby kana text font size bigger smaller">
            <SelectButton
              v-model="furiganaSize"
              :options="TEXT_SIZE_OPTIONS"
              option-label="label"
              option-value="value"
              :allow-empty="false"
              aria-label="Furigana size"
              fluid
            />
          </DisplaySettingRow>
          <DisplaySettingRow
            label="Show hidden furigana on hover"
            for="furiganaOnHover"
            description="Readings hidden by the settings above appear while you hover the word."
            keywords="reading ruby kana reveal mouse peek test"
            inline
          >
            <ToggleSwitch v-model="furiganaOnHover" input-id="furiganaOnHover" />
          </DisplaySettingRow>
          <DisplaySettingRow label="Headword size" keywords="headword text font size bigger smaller">
            <SelectButton
              v-model="headwordSize"
              :options="TEXT_SIZE_OPTIONS"
              option-label="label"
              option-value="value"
              :allow-empty="false"
              aria-label="Word size"
              fluid
            />
          </DisplaySettingRow>
          <DisplaySettingRow label="Example sentence size" keywords="text font size bigger smaller">
            <SelectButton
              v-model="sentenceSize"
              :options="TEXT_SIZE_OPTIONS"
              option-label="label"
              option-value="value"
              :allow-empty="false"
              aria-label="Example sentence size"
              fluid
            />
          </DisplaySettingRow>
          <DisplaySettingRow
            label="Pitch accent"
            description="Word pages and SRS cards. Numbers also show next to words in vocabulary lists."
            keywords="pitch accent graph diagram number both"
          >
            <SelectButton
              v-model="pitchAccentDisplay"
              :options="PITCH_ACCENT_OPTIONS"
              option-label="label"
              option-value="value"
              :allow-empty="false"
              aria-label="Pitch accent"
              fluid
            />
          </DisplaySettingRow>
          <DisplaySettingRow
            label="Colour readings by pitch"
            for="pitchAccentColours"
            description="Colours the furigana, or the whole word when it’s written in kana."
            keywords="pitch accent colour color heiban atamadaka nakadaka odaka"
            inline
          >
            <ToggleSwitch v-model="pitchAccentColours" input-id="pitchAccentColours" />
            <template #preview>
              <ul class="flex flex-wrap gap-x-4 gap-y-1 text-sm" aria-label="Pitch accent colours">
                <li v-for="category in PITCH_CATEGORIES" :key="category" :class="`pitch-${category}`">
                  <Tooltip :content="PITCH_CATEGORY_LABELS[category].description">
                    <span class="pitch-swatch cursor-help font-medium">{{ PITCH_CATEGORY_LABELS[category].name }}</span>
                  </Tooltip>
                </li>
              </ul>
            </template>
          </DisplaySettingRow>
          <DisplaySettingRow
            class="sm:col-span-2"
            label="Japanese font"
            description="Screen fonts draw some kanji differently from how they are written by hand. Textbook uses the shapes taught in Japanese schools."
            keywords="font typeface mincho serif rounded textbook kyokasho gothic installed system custom"
          >
            <JapaneseFontPicker />
          </DisplaySettingRow>
        </div>
      </div>
    </DisplaySection>

    <DisplaySection
      icon="pi pi-palette"
      title="Word colours"
      description="Colour used on the YouTube karaoke page."
      keywords="color colour unknown young due mature mastered redundant blacklisted suspended subtitles watch"
      :resettable="!profiles.isSectionDefault('wordColours')"
      @reset="profiles.resetSection('wordColours')"
    >
      <div class="max-w-md">
        <DisplaySettingRow
          label="Colour words everywhere"
          for="colourWordsByState"
          :description="
            auth.isAuthenticated ? 'Also colours headwords and the target word in example sentences.' : 'Only available signed in.'
          "
          keywords="color"
          inline
        >
          <ToggleSwitch v-model="colourWordsByState" input-id="colourWordsByState" :disabled="!auth.isAuthenticated" />
        </DisplaySettingRow>
      </div>
      <ul class="mt-4 grid grid-cols-1 gap-x-6 gap-y-1 sm:grid-cols-2 lg:grid-cols-3" @mouseleave="highlightedColour = null">
        <li
          v-for="key in WORD_STATE_COLOUR_KEYS"
          :key="key"
          class="flex items-center gap-3 py-1"
          @mouseenter="highlightedColour = key"
          @focusin="highlightedColour = key"
          @focusout="highlightedColour = null"
        >
          <input
            :id="`colour-${key}`"
            type="color"
            class="h-9 w-11 shrink-0 cursor-pointer rounded border border-surface-300 bg-transparent dark:border-surface-600"
            :value="colourInputValue(key)"
            @input="setColour(key, ($event.target as HTMLInputElement).value)"
          />
          <label :for="`colour-${key}`" class="flex min-w-0 flex-1 cursor-pointer items-baseline gap-2 text-sm">
            <span class="text-lg" lang="ja" :style="colours[key] ? wordColourStyle(colours[key]!) : undefined">言葉</span>
            <span class="truncate">{{ WORD_STATE_COLOUR_LABELS[key] }}</span>
          </label>
          <Button
            v-if="colours[key] !== DEFAULT_WORD_STATE_COLOURS[key]"
            icon="pi pi-undo"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Reset the ${WORD_STATE_COLOUR_LABELS[key]} colour`"
            @click="setColour(key, DEFAULT_WORD_STATE_COLOURS[key])"
          />
        </li>
      </ul>
      <WordColourPreview class="mt-4" :highlight="highlightedColour" />
    </DisplaySection>

    <DisplaySection
      icon="pi pi-desktop"
      title="Appearance"
      description="General appearance settings."
      :resettable="!profiles.isSectionDefault('appearance')"
      @reset="profiles.resetSection('appearance')"
    >
      <div class="grid grid-cols-1 gap-x-6 gap-y-5 md:grid-cols-2">
        <DisplaySettingRow label="Theme" description="Auto follows your system theme" keywords="dark mode light mode night">
          <SelectButton v-model="themeMode" :options="THEME_OPTIONS" option-value="value" :allow-empty="false" aria-label="Theme" fluid>
            <template #option="{ option }">
              <Icon :name="option.icon" size="1.1em" aria-hidden="true" />
              <span>{{ option.label }}</span>
            </template>
          </SelectButton>
        </DisplaySettingRow>
        <DisplaySettingRow label="Title language" description="Default title language when available." keywords="name romaji english japanese">
          <SelectButton
            v-model="titleLanguage"
            :options="TITLE_LANGUAGE_OPTIONS"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            aria-label="Title language"
            fluid
          />
          <template #preview>
            <ul class="flex flex-wrap gap-x-4 gap-y-0.5 text-sm" aria-label="Example titles">
              <li v-for="(title, i) in titleExamples" :key="i" class="truncate font-medium text-surface-900 dark:text-surface-0" :lang="titleLanguage === 0 ? 'ja' : undefined">{{ title }}</li>
            </ul>
          </template>
        </DisplaySettingRow>
        <DisplaySettingRow label="Media lists open as" description="Also easily accessible from the media list." keywords="cards compact table grid view layout">
          <SelectButton v-model="displayStyle" :options="LIST_VIEW_OPTIONS" option-value="value" :allow-empty="false" aria-label="Media lists open as" fluid>
            <template #option="{ option }">
              <Icon :name="option.icon" size="1.1em" aria-hidden="true" />
              <span>{{ option.label }}</span>
            </template>
          </SelectButton>
        </DisplaySettingRow>
        <DisplaySettingRow
          label="Animations"
          description="“Always reduce” stops animations even when your device doesn’t ask for it."
          keywords="motion reduce transitions accessibility"
        >
          <SelectButton
            v-model="reducedMotion"
            :options="REDUCED_MOTION_OPTIONS"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            aria-label="Animations"
            fluid
          />
        </DisplaySettingRow>
      </div>
    </DisplaySection>

    <DisplaySection
      icon="pi pi-images"
      title="Media pages"
      description="Difficulty and reading time."
      :resettable="!profiles.isSectionDefault('mediaPages')"
      @reset="profiles.resetSection('mediaPages')"
    >
      <div class="grid grid-cols-1 gap-x-6 gap-y-5 md:grid-cols-3">
        <DisplaySettingRow label="Difficulty style" for="difficultyDisplayStyle" keywords="difficulty name value label">
          <Select
            v-model="difficultyDisplayStyle"
            :options="DIFFICULTY_DISPLAY_STYLE_OPTIONS"
            option-label="label"
            option-value="value"
            input-id="difficultyDisplayStyle"
            fluid
          />
        </DisplaySettingRow>
        <DisplaySettingRow label="Difficulty value" keywords="difficulty percentage percent number scale">
          <SelectButton
            v-model="difficultyValueDisplayStyle"
            :options="DIFFICULTY_VALUE_DISPLAY_STYLE_OPTIONS"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            aria-label="Difficulty value"
            fluid
          />
        </DisplaySettingRow>
        <DisplaySettingRow
          label="Difficulty colours"
          for="difficultyPalette"
          description="Different colour themes to adapt to colour blindness."
          keywords="difficulty colour color blind colorblind deuteranopia protanopia tritanopia palette accessibility"
        >
          <Select v-model="difficultyPalette" :options="DIFFICULTY_PALETTE_OPTIONS" option-label="label" option-value="value" input-id="difficultyPalette" fluid />
        </DisplaySettingRow>
        <ul
          class="flex flex-wrap gap-x-5 gap-y-1 rounded border border-dashed border-surface-300 px-3 py-2 dark:border-surface-600 md:col-span-3"
          aria-label="Example difficulties"
        >
          <li v-for="value in DIFFICULTY_EXAMPLES" :key="value">
            <DifficultyDisplay :difficulty="value" :difficulty-raw="value" />
          </li>
        </ul>
        <DisplaySettingRow
          label="Reading speed"
          for="readingSpeed"
          description="Characters per hour, for reading time estimates."
          keywords="reading time duration hours characters difficulty level per easy hard"
        >
          <InputNumber
            v-if="!readingSpeedByDifficulty"
            v-model="readingSpeed"
            input-id="readingSpeed"
            :min="MIN_READING_SPEED" :max="MAX_READING_SPEED" :step="100" show-buttons fluid />
          <template #preview>
            <p v-if="!readingSpeedByDifficulty" class="text-sm text-surface-600 dark:text-surface-400">
              A {{ NOVEL_CHARACTERS.toLocaleString() }}-character novel takes about
              <span class="font-semibold text-surface-900 dark:text-surface-0">{{ novelHours > 0 ? novelHours.toLocaleString() : '<1' }} h</span>.
            </p>
            <button
              type="button"
              class="flex items-center gap-1.5 self-start rounded text-sm font-medium text-primary-600 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 dark:text-primary-400"
              :aria-expanded="bandSpeedsOpen"
              aria-controls="readingSpeedsByDifficulty"
              @click="bandSpeedsOpen = !bandSpeedsOpen"
            >
              <i class="pi text-xs" :class="bandSpeedsOpen ? 'pi-chevron-down' : 'pi-chevron-right'" aria-hidden="true" />
              Speed per difficulty
              <span v-if="readingSpeedByDifficulty" class="rounded bg-primary-100 px-1.5 text-xs text-primary-700 dark:bg-primary-900/40 dark:text-primary-300">On</span>
            </button>
          </template>
        </DisplaySettingRow>
        <div
          v-if="bandSpeedsOpen"
          id="readingSpeedsByDifficulty"
          class="flex flex-col gap-3 rounded border border-surface-200 p-3 dark:border-surface-700 md:col-span-3"
        >
          <p class="text-xs text-surface-600 dark:text-surface-400">
            Customise your reading speed depending on the difficulty. Estimation are for a
            {{ NOVEL_CHARACTERS.toLocaleString() }}-character novel.
          </p>
          <fieldset class="grid grid-cols-2 gap-x-4 gap-y-3 sm:grid-cols-3 lg:grid-cols-6">
            <legend class="sr-only">Reading speed for each difficulty</legend>
            <div v-for="(name, band) in difficultyNames" :key="name" class="flex min-w-0 flex-col gap-1">
              <label :for="`readingSpeed-${band}`" class="text-sm font-medium" :class="difficultyTextClass(band)">{{ name }}</label>
              <InputNumber
                :model-value="bandSpeed(band)"
                :input-id="`readingSpeed-${band}`"
                :min="MIN_READING_SPEED"
                :max="MAX_READING_SPEED"
                :step="100"
                show-buttons
                fluid
                @update:model-value="setBandSpeed(band, $event)"
              />
              <span class="text-xs tabular-nums text-surface-600 dark:text-surface-400">About {{ bandNovelHours(band).toLocaleString() }} h</span>
            </div>
          </fieldset>
          <button
            type="button"
            class="self-start rounded text-sm font-medium text-primary-600 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 dark:text-primary-400"
            @click="readingSpeedByDifficulty ? useOneSpeed() : suggestBandSpeeds()"
          >
            {{ readingSpeedByDifficulty ? 'Use one speed for everything' : 'Suggest different values depending on the difficulty' }}
          </button>
        </div>
      </div>
    </DisplaySection>

    <DisplaySection
      icon="pi pi-th-large"
      title="Media cards"
      description="Complete customisation of the media card."
      keywords="stats columns genres tags relations description coverage external rating alternative titles"
      :resettable="!profiles.isSectionDefault('mediaCards')"
      @reset="profiles.resetSection('mediaCards')"
    >
      <MediaCardEditor />
    </DisplaySection>

    <DisplaySection
      icon="pi pi-book"
      title="Vocabulary and audio"
      :resettable="!profiles.isSectionDefault('vocabulary')"
      @reset="profiles.resetSection('vocabulary')"
    >
      <div class="grid grid-cols-1 gap-x-8 gap-y-5 md:grid-cols-2 lg:grid-cols-3">
        <div class="row-span-2 grid content-start gap-5 self-start">
          <DisplaySettingRow
            label="Hide definitions in lists"
            for="hideVocabularyDefinitions"
            description="Test yourself on vocabulary lists."
            keywords="meaning quiz"
            inline
          >
            <ToggleSwitch v-model="hideVocabularyDefinitions" input-id="hideVocabularyDefinitions" />
          </DisplaySettingRow>
          <DisplaySettingRow
            v-if="auth.isAuthenticated"
            label="Master in 1 click"
            for="quickMasterVocabulary"
            description="The green + masters a word instead of opening its menu."
            keywords="mastered quick button"
            inline
          >
            <ToggleSwitch v-model="quickMasterVocabulary" input-id="quickMasterVocabulary" />
          </DisplaySettingRow>
          <DisplaySettingRow
            label="Unblur NSFW sentences"
            for="displayAllNsfw"
            description="Show sentences with possible adult content without a blur."
            keywords="adult blur explicit"
            inline
          >
            <ToggleSwitch v-model="displayAllNsfw" input-id="displayAllNsfw" />
          </DisplaySettingRow>
        </div>
        <DisplaySettingRow
          label="Kanji level badge"
          for="kanjiScale"
          description="Shown next to each kanji in a kanji breakdown."
          keywords="jlpt grade kanken wanikani rtk klc tmw"
        >
          <Select v-model="kanjiScale" :options="kanjiScaleOptions" option-label="label" option-value="value" input-id="kanjiScale" fluid />
          <template #preview>
            <ul class="flex flex-wrap gap-2" aria-label="Example kanji breakdown">
              <li
                v-for="kanji in KANJI_EXAMPLES"
                :key="kanji.character"
                class="inline-flex items-center gap-2 rounded-lg border border-surface-200 px-2.5 py-1.5 dark:border-surface-700"
              >
                <span class="text-xl" lang="ja">{{ kanji.character }}</span>
                <span class="flex flex-col text-[10px] leading-tight">
                  <span class="text-surface-600 dark:text-surface-400">{{ kanji.strokes }} strokes</span>
                  <span v-if="kanjiBadge(kanji)" class="text-primary-600 dark:text-primary-400">{{ kanjiBadge(kanji) }}</span>
                </span>
              </li>
            </ul>
          </template>
        </DisplaySettingRow>
        <DisplaySettingRow label="Text-to-speech voice" for="ttsVoice" description="Used for headwords and example sentences." keywords="tts audio speech voice">
          <div class="flex items-center gap-2">
            <Select v-model="ttsVoice" :options="TTS_VOICE_OPTIONS" option-label="label" option-value="value" input-id="ttsVoice" class="min-w-0 flex-1" />
            <Button
              v-if="ttsVoice !== 'system'"
              :icon="isTtsLoading ? 'pi pi-spin pi-spinner' : 'pi pi-volume-up'"
              severity="secondary"
              outlined
              :class="{ '!text-primary-500': isSpeaking }"
              aria-label="Preview the voice"
              @click="speakWord(1002340, 3)"
            />
          </div>
        </DisplaySettingRow>
      </div>
    </DisplaySection>

    <DisplaySection
      icon="pi pi-mobile"
      title="This device"
      description="These settings are highly dependant on your device, so they don't get saved with your profile."
      :resettable="!deviceIsDefault"
      @reset="resetDevice"
    >
      <div class="grid grid-cols-1 gap-x-8 gap-y-5 md:grid-cols-2">
        <DisplaySettingRow label="Text-to-speech volume" keywords="tts audio sound loud quiet">
          <TtsVolumeControl unlabelled />
        </DisplaySettingRow>
        <DisplaySettingRow label="Dictionary font size" description="Definitions from your imported Yomitan dictionaries." keywords="yomitan text size">
          <div class="flex items-center gap-3">
            <DictionaryFontSizeControl />
            <span class="text-sm tabular-nums text-surface-600 dark:text-surface-400">{{ store.customDictionaryFontSize }}px</span>
          </div>
          <template #preview>
            <p class="rounded border border-surface-200 px-3 py-2 dark:border-surface-700" :style="{ fontSize: `${store.customDictionaryFontSize}px` }">
              <span lang="ja">たべる【食べる】</span> to eat; to live on (e.g. a salary)
            </p>
          </template>
        </DisplaySettingRow>
      </div>
    </DisplaySection>
  </div>
</template>

<style scoped>
  /* Segmented choices size to their labels and never wrap them onto two lines. */
  .display-settings :deep(.p-togglebutton) {
    flex: 1 1 auto;
    white-space: nowrap;
  }

  .display-search {
    background: var(--jiten-page-bg);
  }

  .pitch-swatch {
    color: var(--pitch-colour);
  }
</style>
