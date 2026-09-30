import { defineStore } from 'pinia';
import { DisplayStyle } from '~/types';
import { createCookieState } from '~/stores/jitenStore';

export const useDisplayStyleStore = defineStore('displayStyle', () => {
  const displayStyle = createCookieState<DisplayStyle>('display-style', DisplayStyle.Card);

  return { displayStyle };
});
