<script setup lang="ts">
  import { storeToRefs } from 'pinia';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import { kanjiScaleOptions } from '~/data/kanjiGroupings';

  const store = useJitenStore();
  const {
    titleLanguage,
    displayFurigana,
    displayAllNsfw,
    hideVocabularyDefinitions,
    hideCoverageBorders,
    hideGenres,
    hideTags,
    hideRelations,
    hideDescriptions,
    hideExternalRating,
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

  const { speakWord, isSpeaking, isLoading } = useTts(undefined, 'word');

  const settings = ref();
  const isOverSettings = ref(false);
  const isSettingsInteracted = ref(false);
  const mediaSectionsOpen = ref(false);

  const hiddenSectionsCount = computed(
    () =>
      [hideCoverageBorders, hideGenres, hideTags, hideRelations, hideDescriptions, hideExternalRating, hideAlternativeTitles].filter(
        (s) => s.value
      ).length
  );

  const titleLanguageOptions = ref([
    { label: 'Japanese', value: 0 },
    { label: 'Romaji', value: 1 },
    { label: 'English', value: 2 },
  ]);

  const ttsVoiceOptions = ref([
    { label: 'Female 1', value: 'female' },
    { label: 'Female 2', value: 'female2' },
    { label: 'Male 1', value: 'male' },
    { label: 'Male 2', value: 'male2' },
    { label: 'ASMR', value: 'asmr' },
    { label: 'System', value: 'system' },
  ]);

  const difficultyDisplayStyleOptions = ref([
    { label: 'Name only', value: 0 },
    { label: 'Name and value', value: 1 },
    { label: 'Value only', value: 2 },
  ]);

  const difficultyValueDisplayStyleOptions = ref([
    { label: '0 to 5', value: 1 },
    { label: 'Percentage', value: 2 },
  ]);

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
  <Popover ref="settings" @mouseenter="onSettingsMouseEnter" @mouseleave="onSettingsMouseLeave" :pt="{ root: { class: 'w-[90vw] max-w-sm md:w-auto' }, content: { class: 'p-3 md:p-4 max-h-[80vh] overflow-y-auto' } }">
    <div class="flex flex-col gap-2">
      <div class="flex justify-between items-center mb-2">
        <span class="font-semibold text-base">Display Settings</span>
        <Button class="md:hidden" icon="pi pi-times" text rounded size="small" @click="settings.hide()" aria-label="Close settings" />
      </div>
      <FloatLabel variant="on" class="">
        <Select
          v-model="titleLanguage"
          :options="titleLanguageOptions"
          option-label="label"
          option-value="value"
          placeholder="Titles Language"
          input-id="titleLanguage"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="titleLanguage">Titles Language</label>
      </FloatLabel>

      <div class="flex items-center gap-2">
        <FloatLabel variant="on" class="flex-1">
          <Select
            v-model="ttsVoice"
            :options="ttsVoiceOptions"
            option-label="label"
            option-value="value"
            placeholder="TTS Voice"
            input-id="ttsVoice"
            @show="isSettingsInteracted = true"
            @hide="isSettingsInteracted = false"
          />
          <label for="ttsVoice">TTS Voice</label>
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

      <Divider class="!my-1 md:!my-2 !mx-2" />

      <div class="flex items-center gap-2 py-1">
        <Checkbox v-model="displayFurigana" input-id="displayFurigana" name="furigana" :binary="true" />
        <label for="displayFurigana" class="text-sm cursor-pointer">Display Furigana</label>
      </div>

      <div class="flex items-center gap-2 py-1">
        <Checkbox v-model="hideVocabularyDefinitions" input-id="hideVocabularyDefinitions" name="hideVocabularyDefinitions" :binary="true" />
        <label for="hideVocabularyDefinitions" class="text-sm cursor-pointer">Hide Vocabulary Definitions</label>
      </div>

      <div v-if="auth.isAuthenticated" class="flex items-center gap-2 py-1">
        <Checkbox v-model="quickMasterVocabulary" input-id="quickMasterVocabulary" name="quickMasterVocabulary" :binary="true" />
        <label for="quickMasterVocabulary" class="text-sm cursor-pointer">Master in 1 click</label>
      </div>

      <div class="flex items-center gap-2 py-1">
        <Checkbox v-model="displayAllNsfw" input-id="displayAllNsfw" name="nsfw" :binary="true" />
        <label for="displayAllNsfw" class="text-sm cursor-pointer">Unblur all NSFW sentences</label>
      </div>

      <Divider class="!my-1 md:!my-2 !mx-2" />

      <button
        type="button"
        class="flex items-center justify-between w-full py-1 cursor-pointer"
        :aria-expanded="mediaSectionsOpen.toString()"
        @click="mediaSectionsOpen = !mediaSectionsOpen"
      >
        <span class="text-sm font-medium">
          Media page sections
          <span v-if="hiddenSectionsCount > 0" class="font-normal text-muted-color">({{ hiddenSectionsCount }} hidden)</span>
        </span>
        <i class="pi text-xs" :class="mediaSectionsOpen ? 'pi-chevron-up' : 'pi-chevron-down'" />
      </button>

      <div v-if="mediaSectionsOpen" class="flex flex-col gap-2 pl-1">
        <div v-if="auth.isAuthenticated" class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideCoverageBorders" input-id="hideCoverageBorders" name="hideCoverageBorders" :binary="true" />
          <label for="hideCoverageBorders" class="text-sm cursor-pointer">Hide coverage borders</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideGenres" input-id="hideGenres" name="hideGenres" :binary="true" />
          <label for="hideGenres" class="text-sm cursor-pointer">Hide genres</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideTags" input-id="hideTags" name="hideTags" :binary="true" />
          <label for="hideTags" class="text-sm cursor-pointer">Hide tags</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideRelations" input-id="hideRelations" name="hideRelations" :binary="true" />
          <label for="hideRelations" class="text-sm cursor-pointer">Hide relations</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideDescriptions" input-id="hideDescriptions" name="hideDescriptions" :binary="true" />
          <label for="hideDescriptions" class="text-sm cursor-pointer">Hide descriptions</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideExternalRating" input-id="hideExternalRating" name="hideExternalRating" :binary="true" />
          <label for="hideExternalRating" class="text-sm cursor-pointer">Hide external ratings</label>
        </div>

        <div class="flex items-center gap-2 py-1">
          <Checkbox v-model="hideAlternativeTitles" input-id="hideAlternativeTitles" name="hideAlternativeTitles" :binary="true" />
          <label for="hideAlternativeTitles" class="text-sm cursor-pointer">Hide alternative titles</label>
        </div>
      </div>

      <Divider class="!my-1 md:!my-2 !mx-2" />

      <div class="flex flex-col gap-2 md:gap-4">
        <label for="readingSpeed" class="text-sm font-medium">Reading Speed (chars/hour)</label>
        <div class="w-full">
          <InputNumber v-model="readingSpeed" show-buttons :min="100" :max="100000" :step="100" size="small" class="w-full" fluid />
        </div>
        <div class="w-full px-1">
          <Slider v-model="readingSpeed" :min="100" :max="100000" :step="100" class="w-full" />
        </div>
      </div>

      <Divider class="!my-1 md:!my-2 !mx-2" />

      <FloatLabel variant="on" class="">
        <Select
          v-model="difficultyDisplayStyle"
          :options="difficultyDisplayStyleOptions"
          option-label="label"
          option-value="value"
          placeholder="Difficulty Style"
          input-id="difficultyDisplayStyle"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="difficultyDisplayStyle">Difficulty Style</label>
      </FloatLabel>

      <FloatLabel variant="on" class="">
        <Select
          v-model="difficultyValueDisplayStyle"
          :options="difficultyValueDisplayStyleOptions"
          option-label="label"
          option-value="value"
          placeholder="Difficulty Value Style"
          input-id="difficultyValueDisplayStyle"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="difficultyValueDisplayStyle">Difficulty Value Style</label>
      </FloatLabel>

      <FloatLabel variant="on" class="">
        <Select
          v-model="kanjiScale"
          :options="kanjiScaleOptions"
          option-label="label"
          option-value="value"
          placeholder="Kanji breakdown scale"
          input-id="kanjiScale"
          @show="isSettingsInteracted = true"
          @hide="isSettingsInteracted = false"
        />
        <label for="kanjiScale">Kanji breakdown scale</label>
      </FloatLabel>

      <div v-if="auth.isAuthenticated && auth.isAdmin" class="flex items-center gap-2 py-1">
        <Checkbox v-model="displayAdminFunctions" input-id="displayAdminFunctions" name="adminFunctions" :binary="true" />
        <label for="displayAdminFunctions" class="text-sm cursor-pointer">Display admin functions</label>
      </div>

      <template v-if="auth.isAuthenticated">
        <Divider class="!my-1 md:!my-2 !mx-2" />
        <NuxtLink to="/settings" class="flex items-center justify-between py-1 text-sm font-medium" @click="hide()">
          <span>All settings</span>
          <i class="pi pi-arrow-right text-xs" />
        </NuxtLink>
      </template>
    </div>
  </Popover>
</template>
