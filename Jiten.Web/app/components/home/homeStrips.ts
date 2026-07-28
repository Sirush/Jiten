import type { Component } from 'vue';
import HomeWhatsNewStrip from './HomeWhatsNewStrip.vue';
import HomePlusBlock from './HomePlusBlock.vue';
import HomeReaderCard from './HomeReaderCard.vue';

export interface HomeStripEntry {
  id: string;
  component: Component;
}

/**
 * Render order of the logged-in home page's blocks. Each one fetches its own data and renders
 * nothing when it has none, so reordering or removing a row never touches HomeMember.
 */
export const homeStrips: HomeStripEntry[] = [
  { id: 'whats-new', component: HomeWhatsNewStrip },
  { id: 'plus', component: HomePlusBlock },
  { id: 'reader', component: HomeReaderCard },
];
