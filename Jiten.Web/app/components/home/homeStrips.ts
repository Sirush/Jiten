import type { Component } from 'vue';
import HomeReaderCard from './HomeReaderCard.vue';
import HomeMpvCard from './HomeMpvCard.vue';

export interface HomeStripEntry {
  id: string;
  component: Component;
}

/**
 * Render order of the logged-in home page's promo blocks, below the grouped strip card that
 * HomeMember owns. Each one renders nothing when it has no data, so reordering never touches HomeMember.
 */
export const homeStrips: HomeStripEntry[] = [
  { id: 'reader', component: HomeReaderCard },
  { id: 'mpv', component: HomeMpvCard },
];
