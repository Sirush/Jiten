import { defineStore } from 'pinia';
import { TitleLanguage } from '~/types';

export const useJitenStore = defineStore('jiten', () => {
  const titleLanguageCookie = useCookie<TitleLanguage>('jiten-title-language', {
    default: () => TitleLanguage.Romaji,
    watch: true,
    maxAge: 60 * 60 * 24 * 365, // 1 year
    path: '/',
  });

  const titleLanguage = ref<TitleLanguage>(titleLanguageCookie.value);

  watch(titleLanguage, (newValue) => {
    titleLanguageCookie.value = newValue;
  });

  const displayFuriganaCookie = useCookie<boolean>('jiten-display-furigana', {
    default: () => true,
    watch: true,
    maxAge: 60 * 60 * 24 * 365, // 1 year
    path: '/',
  });

  const displayFurigana = ref<boolean>(displayFuriganaCookie.value);

  watch(displayFurigana, (newValue) => {
    displayFuriganaCookie.value = newValue;
  });

  const darkModeCookie = useCookie<boolean>('jiten-dark-mode', {
    default: () => false,
    watch: true,
    maxAge: 60 * 60 * 24 * 365, // 1 year
    path: '/',
  });

  const darkMode = ref<boolean>(darkModeCookie.value);

  watch(darkMode, (newValue) => {
    darkModeCookie.value = newValue;
  });

  return { titleLanguage, displayFurigana, darkMode };
});
