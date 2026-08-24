import type { Component } from 'vue';
import HomeWhatsNewStrip from './HomeWhatsNewStrip.vue';
import HomePollCard from './HomePollCard.vue';
import HomePlusBlock from './HomePlusBlock.vue';
import HomeReaderCard from './HomeReaderCard.vue';
import HomeMpvCard from './HomeMpvCard.vue';

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
  { id: 'poll', component: HomePollCard },
  { id: 'plus', component: HomePlusBlock },
  { id: 'reader', component: HomeReaderCard },
  { id: 'mpv', component: HomeMpvCard },
];
