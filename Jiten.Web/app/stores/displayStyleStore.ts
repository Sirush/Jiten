import { defineStore } from 'pinia';
import { DisplayStyle } from '~/types';

export const useDisplayStyleStore = defineStore('displayStyle', () => {
  const displayStyleCookie = useCookie<DisplayStyle>('jiten-display-style', {
    watch: true,
    maxAge: 60 * 60 * 24 * 365, // 1 year
    path: '/',
  });

  const displayStyle = ref<DisplayStyle>(displayStyleCookie.value ?? DisplayStyle.Card);

  watch(displayStyle, (newValue) => {
    displayStyleCookie.value = newValue;
  });

  return { displayStyle };
});
