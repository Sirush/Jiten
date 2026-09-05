<script setup lang="ts">
  import InputText from 'primevue/inputtext';
  import ProgressSpinner from 'primevue/progressspinner';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import type { MediaSuggestion } from '~/types/types';
  import { TitleLanguage } from '~/types';
  import { looksLikeDescription } from '~/utils/describeQuery';

  const props = withDefaults(
    defineProps<{
      placeholder?: string;
      autofocus?: boolean;
      /** Prefill from ?text= — right on /parse, wrong for a modal that should open empty. */
      seedFromRoute?: boolean;
    }>(),
    { placeholder: undefined, autofocus: false, seedFromRoute: true }
  );

  const route = useRoute();
  const store = useJitenStore();

  const searchText = ref<string>(props.seedFromRoute ? (Array.isArray(route.query.text) ? route.query.text[0] || '' : (route.query.text as string) || '') : '');
  const isDropdownOpen = ref(false);
  const highlightedIndex = ref(0);

  const { suggestions, totalCount, isLoading, fetchSuggestions, clearSuggestions } = useMediaSuggestions();
  const { response: describeResponse, isLoading: describeLoading, search: describeSearch, clear: describeClear } = useDescriptionSearch(3);
  const localiseTitle = useLocaliseTitle();

  const extractFirstKanji = (text: string): string | null => {
    const kanjiRegex = /[\u4e00-\u9faf\u3400-\u4dbf]/;
    const match = text.match(kanjiRegex);
    return match ? match[0] : null;
  };

  const kanjiSearchTarget = computed(() => {
    const text = searchText.value.trim();
    const kanjiModifierMatch = text.match(/^(.+?)\s*#kanji$/i);
    if (!kanjiModifierMatch) return null;

    const searchPart = kanjiModifierMatch[1];
    return extractFirstKanji(searchPart);
  });

  const showMediaSection = computed(() => searchText.value.length >= 2);

  // Description matches earn a place when the text reads like one, or when title matches run
  // thin: both are moments where a plain "no media found" would be a dead end.
  const HANDOFF_BELOW = 3;
  const readsLikeDescription = computed(() => looksLikeDescription(searchText.value));
  const titlesThin = computed(() => !isLoading.value && suggestions.value.length < HANDOFF_BELOW);
  const describeResults = computed(() => {
    if (!showMediaSection.value || kanjiSearchTarget.value) return [];
    if (!readsLikeDescription.value && !titlesThin.value) return [];
    return describeResponse.value?.results ?? [];
  });
  const describeCount = computed(() => describeResults.value.length);

  watch(searchText, (newValue) => {
    if (newValue && newValue.length >= 1) {
      isDropdownOpen.value = true;
      if (newValue.length >= 2) {
        fetchSuggestions(newValue);
        if (looksLikeDescription(newValue)) describeSearch(newValue);
        else describeClear();
      } else {
        clearSuggestions();
        describeClear();
      }
    } else {
      isDropdownOpen.value = false;
      clearSuggestions();
      describeClear();
    }
    highlightedIndex.value = 0;
  });

  watch([isLoading, suggestions], ([loading, list]) => {
    if (!loading && list.length < HANDOFF_BELOW && searchText.value.length >= 2 && !readsLikeDescription.value) {
      describeSearch(searchText.value);
    }
  });

  // Rows are real links so middle and ctrl click open a tab; keyboard selection reuses the same targets.
  const trimmedText = computed(() => searchText.value.trim());
  const describeRoute = computed(() => ({ path: '/decks/media', query: { describe: trimmedText.value } }));
  const parseRoute = computed(() => ({ path: '/parse', query: { text: trimmedText.value } }));
  const mediaSearchRoute = computed(() => ({ path: '/decks/media', query: { title: trimmedText.value } }));
  const deckRoute = (deckId: number) => `/decks/media/${deckId}/detail`;
  const kanjiRoute = (character: string) => `/kanji/${encodeURIComponent(character)}`;

  const closeDropdown = () => {
    isDropdownOpen.value = false;
  };

  const navigateToParse = async () => {
    if (!trimmedText.value) return;
    closeDropdown();
    await navigateTo(parseRoute.value);
  };

  const navigateToMediaSearch = async () => {
    if (!trimmedText.value) return;
    closeDropdown();
    await navigateTo(mediaSearchRoute.value);
  };

  const navigateToDeck = async (deckId: number) => {
    closeDropdown();
    await navigateTo(deckRoute(deckId));
  };

  const navigateToKanji = async (character: string) => {
    closeDropdown();
    await navigateTo(kanjiRoute(character));
  };

  // Row order: primary action, "view more media", title suggestions, description matches (if any).
  const VIEW_MORE_INDEX = 1;
  const suggestionIndex = (index: number) => 2 + index;
  const describeStart = computed(() => (showMediaSection.value && suggestions.value.length > 0 ? 2 + suggestions.value.length : 1));
  const describeIndex = (index: number) => describeStart.value + index;

  const totalOptions = computed(() => describeStart.value + describeCount.value);

  const handleKeyDown = (event: KeyboardEvent) => {
    if (!isDropdownOpen.value && searchText.value.length >= 1) {
      if (event.key === 'ArrowDown') {
        isDropdownOpen.value = true;
        return;
      }
    }

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        highlightedIndex.value = (highlightedIndex.value + 1) % totalOptions.value;
        break;
      case 'ArrowUp':
        event.preventDefault();
        highlightedIndex.value = highlightedIndex.value === 0 ? totalOptions.value - 1 : highlightedIndex.value - 1;
        break;
      case 'Enter':
        event.preventDefault();
        handleSelection();
        break;
      case 'Escape':
        isDropdownOpen.value = false;
        break;
    }
  };

  const handleSelection = () => {
    if (highlightedIndex.value === 0) {
      if (kanjiSearchTarget.value) {
        navigateToKanji(kanjiSearchTarget.value);
      } else {
        navigateToParse();
      }
    } else if (highlightedIndex.value >= describeStart.value) {
      const match = describeResults.value[highlightedIndex.value - describeStart.value];
      if (match) navigateToDeck(match.deck.deckId);
    } else if (showMediaSection.value && suggestions.value.length > 0 && highlightedIndex.value === VIEW_MORE_INDEX) {
      navigateToMediaSearch();
    } else if (showMediaSection.value && suggestions.value.length > 0) {
      const index = highlightedIndex.value - 2;
      if (suggestions.value[index]) {
        navigateToDeck(suggestions.value[index].deckId);
      }
    }
  };

  const dropdownRef = ref<HTMLElement | null>(null);
  const inputRef = ref<HTMLElement | null>(null);

  onMounted(() => {
    document.addEventListener('click', handleClickOutside);
    // Native autofocus dies on hydration/client-side navigation; skipped on touch devices where it would pop the keyboard over the page.
    if (props.autofocus && window.matchMedia('(pointer: fine)').matches) {
      inputRef.value?.querySelector('input')?.focus();
    }
  });

  onUnmounted(() => {
    document.removeEventListener('click', handleClickOutside);
  });

  const handleClickOutside = (event: MouseEvent) => {
    const target = event.target as Node;
    if (dropdownRef.value && !dropdownRef.value.contains(target) && inputRef.value && !inputRef.value.contains(target)) {
      isDropdownOpen.value = false;
    }
  };

  const getTitle = (suggestion: MediaSuggestion): string => {
    if (store.titleLanguage === TitleLanguage.Original) {
      return suggestion.originalTitle;
    }

    if (store.titleLanguage === TitleLanguage.Romaji) {
      return suggestion.romajiTitle || suggestion.originalTitle;
    }

    if (store.titleLanguage === TitleLanguage.English) {
      return suggestion.englishTitle || suggestion.romajiTitle || suggestion.originalTitle;
    }

    return suggestion.originalTitle;
  };

  const isOriginalTitle = (suggestion: MediaSuggestion): boolean => getTitle(suggestion) === suggestion.originalTitle;

  const getCoverUrl = (coverName: string): string => {
    return coverName === 'nocover.jpg' ? '/img/nocover.jpg' : coverName;
  };

  const remainingCount = computed(() => {
    return Math.max(0, totalCount.value - suggestions.value.length);
  });
</script>

<template>
  <div class="relative w-full">
    <div ref="inputRef" class="flex flex-row search-container">
      <IconField class="w-full">
        <InputIcon>
          <Icon name="material-symbols:search-rounded" />
        </InputIcon>
        <InputText
          v-model="searchText"
          type="text"
          lang="ja"
          :placeholder="placeholder || 'Search words, sentences, or media. Use * for wildcard'"
          class="w-full text-sm sm:text-base"
          maxlength="2000"
          :autofocus="autofocus"
          role="combobox"
          aria-autocomplete="list"
          :aria-expanded="isDropdownOpen"
          aria-controls="omni-search-dropdown"
          @keydown="handleKeyDown"
          @focus="searchText.length >= 1 && (isDropdownOpen = true)"
        />
      </IconField>
      <Button label="Search" class="ml-2" :disabled="!searchText.trim()" @click="navigateToParse">
        <Icon name="material-symbols:search-rounded" />
      </Button>
    </div>

    <Transition name="fade">
      <div
        v-if="isDropdownOpen && searchText.length >= 1"
        id="omni-search-dropdown"
        ref="dropdownRef"
        class="absolute z-50 w-full mt-1 bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-lg shadow-lg overflow-hidden"
        role="listbox"
      >
        <!-- Primary Action (index 0) -->
        <NuxtLink
          v-if="kanjiSearchTarget"
          :to="kanjiRoute(kanjiSearchTarget)"
          class="px-4 py-3 cursor-pointer flex items-center gap-3 transition-colors"
          :class="highlightedIndex === 0 ? 'bg-purple-100 dark:bg-purple-900/30' : 'hover:bg-gray-100 dark:hover:bg-gray-700'"
          role="option"
          :aria-selected="highlightedIndex === 0"
          @click="closeDropdown"
          @mouseenter="highlightedIndex = 0"
        >
          <span class="text-2xl font-bold text-purple-500" lang="ja">{{ kanjiSearchTarget }}</span>
          <div class="min-w-0 flex-1">
            <div class="font-medium">
              View kanji: <span lang="ja">{{ kanjiSearchTarget }}</span>
            </div>
            <div class="text-sm text-gray-500 dark:text-gray-400">Go to kanji details page</div>
          </div>
          <div class="text-xs text-gray-400 dark:text-gray-400">
            <kbd class="px-1.5 py-0.5 bg-gray-200 dark:bg-gray-700 rounded font-mono text-xs">Enter</kbd>
          </div>
        </NuxtLink>
        <NuxtLink
          v-else
          :to="parseRoute"
          class="px-4 py-3 cursor-pointer flex items-center gap-3 transition-colors"
          :class="highlightedIndex === 0 ? 'bg-purple-100 dark:bg-purple-900/30' : 'hover:bg-gray-100 dark:hover:bg-gray-700'"
          role="option"
          :aria-selected="highlightedIndex === 0"
          @click="closeDropdown"
          @mouseenter="highlightedIndex = 0"
        >
          <Icon name="material-symbols:search-rounded" class="text-xl text-purple-500" />
          <div class="min-w-0 flex-1">
            <div class="font-medium">
              Search: "<span lang="ja">{{ searchText }}</span
              >"
            </div>
            <div class="text-sm text-gray-500 dark:text-gray-400">Search dictionary by meaning or wildcard. Use #kanji to view kanji details</div>
          </div>
          <div class="text-xs text-gray-400 dark:text-gray-400">
            <kbd class="px-1.5 py-0.5 bg-gray-200 dark:bg-gray-700 rounded font-mono text-xs">Enter</kbd>
          </div>
        </NuxtLink>

        <!-- Media Section -->
        <template v-if="showMediaSection">
          <NuxtLink
            v-if="suggestions.length > 0"
            :to="mediaSearchRoute"
            class="px-4 py-2.5 cursor-pointer flex items-center gap-3 border-t border-gray-100 dark:border-gray-700 transition-colors"
            :class="highlightedIndex === VIEW_MORE_INDEX ? 'bg-purple-100 dark:bg-purple-900/30' : 'hover:bg-gray-100 dark:hover:bg-gray-700'"
            role="option"
            :aria-selected="highlightedIndex === VIEW_MORE_INDEX"
            @click="closeDropdown"
            @mouseenter="highlightedIndex = VIEW_MORE_INDEX"
          >
            <Icon name="material-symbols:video-library-outline" class="text-lg text-gray-500 dark:text-gray-400" />
            <span class="flex-1">
              View more media for "<span lang="ja">{{ searchText }}</span
              >"
              <span v-if="remainingCount > 0" class="text-purple-500 font-medium">(+{{ remainingCount }})</span>
            </span>
            <Icon name="material-symbols:arrow-forward" class="text-gray-400" />
          </NuxtLink>

          <div v-if="isLoading" class="px-4 py-3 flex items-center justify-center border-t border-gray-100 dark:border-gray-700">
            <ProgressSpinner style="width: 20px; height: 20px" stroke-width="4" />
            <span class="ml-2 text-sm text-gray-500 dark:text-gray-400">Searching media...</span>
          </div>

          <template v-else-if="suggestions.length > 0">
            <div class="border-t border-gray-100 dark:border-gray-700">
              <div class="px-4 py-1.5 text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wide">Media</div>
              <NuxtLink
                v-for="(suggestion, index) in suggestions"
                :key="suggestion.deckId"
                :to="deckRoute(suggestion.deckId)"
                class="px-4 py-2 cursor-pointer flex items-center gap-3 transition-colors"
                :class="highlightedIndex === suggestionIndex(index) ? 'bg-purple-100 dark:bg-purple-900/30' : 'hover:bg-gray-100 dark:hover:bg-gray-700'"
                role="option"
                :aria-selected="highlightedIndex === suggestionIndex(index)"
                @click="closeDropdown"
                @mouseenter="highlightedIndex = suggestionIndex(index)"
              >
                <img :src="getCoverUrl(suggestion.coverName)" :alt="getTitle(suggestion)" class="w-10 h-14 object-cover rounded flex-shrink-0" />
                <div class="min-w-0 flex-1">
                  <div class="font-medium truncate" :lang="isOriginalTitle(suggestion) ? 'ja' : undefined">
                    {{ getTitle(suggestion) }}
                  </div>
                  <div class="text-sm text-gray-500 dark:text-gray-400">
                    {{ getMediaTypeText(suggestion.mediaType) }}
                  </div>
                </div>
              </NuxtLink>
            </div>
          </template>

          <div v-else-if="!isLoading" class="px-4 py-3 text-sm text-gray-500 dark:text-gray-400 border-t border-gray-100 dark:border-gray-700">
            No titles match "<span lang="ja">{{ searchText }}</span
            >"<template v-if="describeLoading">, checking descriptions...</template>
          </div>
        </template>

        <!-- Description matches -->
        <div v-if="describeResults.length > 0" class="border-t border-gray-100 dark:border-gray-700">
          <div class="px-4 py-1.5 flex items-center justify-between gap-3">
            <span class="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wide">Media closest by description</span>
            <NuxtLink :to="describeRoute" class="text-xs font-medium text-purple-500 hover:underline cursor-pointer" @click="closeDropdown">See all</NuxtLink>
          </div>
          <NuxtLink
            v-for="(match, index) in describeResults"
            :key="match.deck.deckId"
            :to="deckRoute(match.deck.deckId)"
            class="px-4 py-2 cursor-pointer flex items-center gap-3 transition-colors"
            :class="highlightedIndex === describeIndex(index) ? 'bg-purple-100 dark:bg-purple-900/30' : 'hover:bg-gray-100 dark:hover:bg-gray-700'"
            role="option"
            :aria-selected="highlightedIndex === describeIndex(index)"
            @click="closeDropdown"
            @mouseenter="highlightedIndex = describeIndex(index)"
          >
            <img :src="getCoverUrl(match.deck.coverName || 'nocover.jpg')" :alt="localiseTitle(match.deck)" class="w-10 h-14 object-cover rounded flex-shrink-0" />
            <div class="min-w-0 flex-1">
              <div class="font-medium truncate" :lang="localiseTitle(match.deck) === match.deck.originalTitle ? 'ja' : undefined">
                {{ localiseTitle(match.deck) }}
              </div>
              <div class="text-sm text-gray-500 dark:text-gray-400 truncate">
                {{ getMediaTypeText(match.deck.mediaType) }}<template v-if="match.deck.description"> · {{ match.deck.description }}</template>
              </div>
            </div>
          </NuxtLink>
        </div>
        <div v-else-if="describeLoading && (readsLikeDescription || (titlesThin && suggestions.length > 0))" class="px-4 py-3 flex items-center justify-center border-t border-gray-100 dark:border-gray-700">
          <ProgressSpinner style="width: 20px; height: 20px" stroke-width="4" />
          <span class="ml-2 text-sm text-gray-500 dark:text-gray-400">Matching descriptions...</span>
        </div>
      </div>
    </Transition>
  </div>
</template>

<style scoped>
  /* Rows are links for middle click only; the global anchor colour and underline would restyle them. */
  #omni-search-dropdown :deep(a[role='option']) {
    color: inherit;
    text-decoration: none;
  }

  .fade-enter-active,
  .fade-leave-active {
    transition: opacity 0.15s ease;
  }

  .fade-enter-from,
  .fade-leave-to {
    opacity: 0;
  }

  .search-container :deep(input::placeholder) {
    font-size: 0.7rem;
  }

  @media (min-width: 640px) {
    .search-container :deep(input::placeholder) {
      font-size: 1rem;
    }
  }
</style>
