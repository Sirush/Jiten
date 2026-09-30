<script setup lang="ts">
  import { storeToRefs } from 'pinia';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import { kanjiScaleOptions } from '~/data/kanjiGroupings';
  import { useDisplayProfileStore } from '~/stores/displayProfileStore';
  import {
    DIFFICULTY_DISPLAY_STYLE_OPTIONS,
    DIFFICULTY_VALUE_DISPLAY_STYLE_OPTIONS,
    HEADWORD_FURIGANA_OPTIONS,
    SENTENCE_FURIGANA_OPTIONS,
    TITLE_LANGUAGE_OPTIONS,
    TTS_VOICE_OPTIONS,
  } from '~/utils/displaySettingOptions';

  const store = useJitenStore();
  const {
    titleLanguage,
    headwordFurigana,
    sentenceFurigana,
    displayAllNsfw,
    hideVocabularyDefinitions,
    hideCoverageBorders,
    hideGenres,
    hideTags,
    hideRelations,
    hideDescriptions,
    externalRatingHidden,
    hideAlternativeTitles,
    quickMasterVocabulary,
    displayAdminFunctions,
    readingSpeed,
    difficultyDisplayStyle,
    difficultyValueDisplayStyle,
    kanjiScale,
    ttsVoice,
  } = storeToRefs(store);
  const auth = useAuthStore();
  const displayProfiles = useDisplayProfileStore();
  const profileOptions = computed(() => displayProfiles.profiles.map((p) => ({ label: p.name, value: p.id })));
  const activeProfileId = computed({
    get: () => displayProfiles.activeId,
    set: (id: string | null) => {
      if (id) displayProfiles.switchTo(id);
    },
  });

  const { speakWord, isSpeaking, isLoading } = useTts(undefined, 'word');

  const settings = ref();
  const isOverSettings = ref(false);
  const isSettingsInteracted = ref(false);
  const mediaSectionsOpen = ref(false);

  // Coverage indicators only count while its checkbox is rendered, or a logged-out visitor reads a
  // count they have no way to see or clear.
  const hiddenSectionsCount = computed(() => {
    const toggles = [hideGenres, hideTags, hideRelations, hideDescriptions, externalRatingHidden, hideAlternativeTitles];
    if (auth.isAuthenticated) toggles.push(hideCoverageBorders);
    return toggles.filter((s) => s.value).length;
  });

  const onSettingsMouseEnter = () => {
    isOverSettings.value = true;
  };

  const onSettingsMouseLeave = () => {
    isOverSettings.value = false;
    setTimeout(() => {
      if (!isOverSettings.value && !isSettingsInteracted.value) {
        settings.value.hide();
      }
    }, 750);
  };

  const toggle = (event: boolean) => {
    settings.value.toggle(event);
  };

  const show = (event: boolean) => {
    settings.value.show(event);
  };

  const hide = () => {
    settings.value.hide();
  };

  defineExpose({ toggle, show, hide });
</script>

<template>
  <Popover
    ref="settings"
    :pt="{ root: { class: 'w-[90vw] max-w-sm md:w-auto' }, content: { class: 'p-3 md:p-4 max-h-[80vh] overflow-y-auto' } }"
    @mouseenter="onSettingsMouseEnter"
    @mouseleave="onSettingsMouseLeave"
  >
    <div class="flex flex-col gap-2">
      <div class="flex justify-between items-center mb-2">
        <span class="font-semibold text-base">Display settings</span>
        <Button class="md:hidden" icon="pi pi-times" text rounded size="small" aria-label="Close settings" @click="settings.hide()" />
      </div>
      <FloatLabel v-if="auth.isAuthenticated && displayProfiles.profiles.length > 1" variant="on">
        <Select
          v-model="activeProfileId"
          :options="profileOptions"
          option-label="label"
          option-value="value"
          input-id="displayProfile"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="displayProfile">Profile</label>
      </FloatLabel>

      <FloatLabel variant="on" class="">
        <Select
          v-model="titleLanguage"
          :options="TITLE_LANGUAGE_OPTIONS"
          option-label="label"
          option-value="value"
          placeholder="Title language"
          input-id="titleLanguage"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="titleLanguage">Title language</label>
      </FloatLabel>

      <div class="flex items-center gap-2">
        <FloatLabel variant="on" class="flex-1">
          <Select
            v-model="ttsVoice"
            :options="TTS_VOICE_OPTIONS"
            option-label="label"
            option-value="value"
            placeholder="Text-to-speech voice"
            input-id="ttsVoice"
            @show="isSettingsInteracted = true"
            @hide="isSettingsInteracted = false"
          />
          <label for="ttsVoice">TTS voice</label>
        </FloatLabel>
        <button
          v-if="ttsVoice !== 'system'"
          type="button"
          class="inline-flex items-center justify-center text-surface-400 hover:text-primary-500 transition-colors cursor-pointer p-1"
          :class="{ '!text-primary-500': isSpeaking }"
          title="Preview voice"
          @click="speakWord(1002340, 3)"
        >
          <i v-if="isLoading" class="pi pi-spin pi-spinner text-base" />
          <i v-else class="pi pi-volume-up text-base" />
        </button>
      </div>

      <TtsVolumeControl class="py-1" @interact-start="isSettingsInteracted = true" @interact-end="isSettingsInteracted = false" />

      <Divider class="!my-1 md:!my-2 !mx-2" />

      <FloatLabel variant="on">
        <Select
          v-model="headwordFurigana"
          :options="HEADWORD_FURIGANA_OPTIONS"
          option-label="label"
          option-value="value"
          input-id="headwordFurigana"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="headwordFurigana">Furigana on words</label>
      </FloatLabel>

      <FloatLabel variant="on">
        <Select
          v-model="sentenceFurigana"
          :options="SENTENCE_FURIGANA_OPTIONS"
          option-label="label"
          option-value="value"
          input-id="sentenceFurigana"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="sentenceFurigana">Furigana in example sentences</label>
      </FloatLabel>

      <div class="flex items-center gap-2 py-1">
        <Checkbox v-model="hideVocabularyDefinitions" input-id="hideVocabularyDefinitions" name="hideVocabularyDefinitions" :binary="true" />
        <label for="hideVocabularyDefinitions" class="text-sm cursor-pointer">Hide definitions in lists</label>
      </div>

      <div v-if="auth.isAuthenticated" class="flex items-center gap-2 py-1">
        <Checkbox v-model="quickMasterVocabulary" input-id="quickMasterVocabulary" name="quickMasterVocabulary" :binary="true" />
        <label for="quickMasterVocabulary" class="text-sm cursor-pointer">Master in 1 click</label>
      </div>

      <div class="flex items-center gap-2 py-1">
        <Checkbox v-model="displayAllNsfw" input-id="displayAllNsfw" name="nsfw" :binary="true" />
        <label for="displayAllNsfw" class="text-sm cursor-pointer">Unblur NSFW sentences</label>
      </div>

      <Divider class="!my-1 md:!my-2 !mx-2" />

      <button
        type="button"
        class="flex items-center justify-between w-full py-1 cursor-pointer"
        :aria-expanded="mediaSectionsOpen.toString()"
        @click="mediaSectionsOpen = !mediaSectionsOpen"
      >
        <span class="text-sm font-medium">
          Hide on media pages
          <span v-if="hiddenSectionsCount > 0" class="font-normal text-muted-color">({{ hiddenSectionsCount }} hidden)</span>
        </span>
        <i class="pi text-xs" :class="mediaSectionsOpen ? 'pi-chevron-up' : 'pi-chevron-down'" />
      </button>

      <div v-if="mediaSectionsOpen" class="flex flex-col gap-2 pl-1">
        <div v-if="auth.isAuthenticated" class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideCoverageBorders" input-id="hideCoverageBorders" name="hideCoverageBorders" :binary="true" />
          <label for="hideCoverageBorders" class="text-sm cursor-pointer">Coverage indicators</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideGenres" input-id="hideGenres" name="hideGenres" :binary="true" />
          <label for="hideGenres" class="text-sm cursor-pointer">Genres</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideTags" input-id="hideTags" name="hideTags" :binary="true" />
          <label for="hideTags" class="text-sm cursor-pointer">Tags</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideRelations" input-id="hideRelations" name="hideRelations" :binary="true" />
          <label for="hideRelations" class="text-sm cursor-pointer">Relations</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideDescriptions" input-id="hideDescriptions" name="hideDescriptions" :binary="true" />
          <label for="hideDescriptions" class="text-sm cursor-pointer">Descriptions</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="externalRatingHidden" input-id="hideExternalRating" name="hideExternalRating" :binary="true" />
          <label for="hideExternalRating" class="text-sm cursor-pointer">External ratings</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideAlternativeTitles" input-id="hideAlternativeTitles" name="hideAlternativeTitles" :binary="true" />
          <label for="hideAlternativeTitles" class="text-sm cursor-pointer">Alternative titles</label>
        </div>
      </div>

      <Divider class="!my-1 md:!my-2 !mx-2" />

      <div class="flex flex-col gap-2 md:gap-4">
        <label for="readingSpeed" class="text-sm font-medium">Reading speed (characters per hour)</label>
        <p v-if="store.readingSpeedByDifficulty" class="text-xs text-surface-600 dark:text-surface-400">
          Set per difficulty in
          <NuxtLink to="/settings/display" class="text-primary-600 underline dark:text-primary-400" @click="hide()">display settings</NuxtLink>.
        </p>
        <template v-else>
          <div class="w-full">
            <InputNumber v-model="readingSpeed" show-buttons :min="100" :max="100000" :step="100" size="small" class="w-full" fluid />
          </div>
          <div class="w-full px-1">
            <Slider v-model="readingSpeed" :min="100" :max="100000" :step="100" class="w-full" />
          </div>
        </template>
      </div>

      <Divider class="!my-1 md:!my-2 !mx-2" />

      <FloatLabel variant="on" class="">
        <Select
          v-model="difficultyDisplayStyle"
          :options="DIFFICULTY_DISPLAY_STYLE_OPTIONS"
          option-label="label"
          option-value="value"
          placeholder="Difficulty style"
          input-id="difficultyDisplayStyle"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="difficultyDisplayStyle">Difficulty style</label>
      </FloatLabel>

      <FloatLabel variant="on" class="">
        <Select
          v-model="difficultyValueDisplayStyle"
          :options="DIFFICULTY_VALUE_DISPLAY_STYLE_OPTIONS"
          option-label="label"
          option-value="value"
          placeholder="Difficulty value"
          input-id="difficultyValueDisplayStyle"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="difficultyValueDisplayStyle">Difficulty value</label>
      </FloatLabel>

      <FloatLabel variant="on" class="">
        <Select
          v-model="kanjiScale"
          :options="kanjiScaleOptions"
          option-label="label"
          option-value="value"
          placeholder="Kanji level badge"
          input-id="kanjiScale"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="kanjiScale">Kanji level badge</label>
      </FloatLabel>

      <div v-if="auth.isAuthenticated && auth.isAdmin" class="flex items-center gap-2 py-1">
        <Checkbox v-model="displayAdminFunctions" input-id="displayAdminFunctions" name="adminFunctions" :binary="true" />
        <label for="displayAdminFunctions" class="text-sm cursor-pointer">Display admin functions</label>
      </div>

      <Divider class="!my-1 md:!my-2 !mx-2" />
      <NuxtLink to="/settings/display" class="flex items-center justify-between py-1 text-sm font-medium" @click="hide()">
        <span>More display settings</span>
        <i class="pi pi-sliders-h text-xs" aria-hidden="true" />
      </NuxtLink>
    </div>
  </Popover>
</template>
