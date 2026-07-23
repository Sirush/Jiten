import type { Component } from 'vue';
import type { CardBlockOptions, CardBlockType } from '~/types';
import {
  cardImageDefaults,
  confusableReadingsDefaults,
  customMeaningDefaults,
  deckOccurrencesDefaults,
  definitionsDefaults,
  dividerDefaults,
  etymologyDefaults,
  exampleSentenceDefaults,
  frequencyRankDefaults,
  headwordDefaults,
  kanjiBreakdownDefaults,
  pitchAccentDefaults,
  wordCompositionDefaults,
  wordUsedInDefaults,
} from './cardBlockOptions';
import CardBlockCardStatus from './CardBlockCardStatus.vue';
import CardBlockHeadword from './CardBlockHeadword.vue';
import CardBlockCardImage from './CardBlockCardImage.vue';
import CardBlockExampleSentence from './CardBlockExampleSentence.vue';
import CardBlockConfusableReadings from './CardBlockConfusableReadings.vue';
import CardBlockFrequencyRank from './CardBlockFrequencyRank.vue';
import CardBlockEtymology from './CardBlockEtymology.vue';
import CardBlockDefinitions from './CardBlockDefinitions.vue';
import CardBlockCustomMeaning from './CardBlockCustomMeaning.vue';
import CardBlockPitchAccent from './CardBlockPitchAccent.vue';
import CardBlockKanjiBreakdown from './CardBlockKanjiBreakdown.vue';
import CardBlockWordComposition from './CardBlockWordComposition.vue';
import CardBlockWordUsedIn from './CardBlockWordUsedIn.vue';
import CardBlockDeckOccurrences from './CardBlockDeckOccurrences.vue';
import CardBlockDivider from './CardBlockDivider.vue';

export interface CardBlockDef {
  component: Component;
  label: string;
  icon: string;
  defaultOptions: CardBlockOptions;
  /** Shows the reading — masked while a write-in reading answer is being typed. */
  revealsReading: boolean;
  /** Shows the meaning — masked while a write-in meaning answer is being typed. */
  revealsMeaning: boolean;
  /** Drives the editor's front-side "this reveals the answer" warning. */
  revealsAnswer: boolean;
  singletonPerSide?: boolean;
}

export const cardBlockRegistry: Record<CardBlockType, CardBlockDef> = {
  cardStatus: {
    component: CardBlockCardStatus,
    label: 'Card status',
    icon: 'pi pi-flag',
    defaultOptions: {},
    revealsReading: false,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  headword: {
    component: CardBlockHeadword,
    label: 'Headword',
    icon: 'pi pi-language',
    defaultOptions: headwordDefaults,
    revealsReading: true,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  cardImage: {
    component: CardBlockCardImage,
    label: 'Card image',
    icon: 'pi pi-image',
    defaultOptions: cardImageDefaults,
    revealsReading: false,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  exampleSentence: {
    component: CardBlockExampleSentence,
    label: 'Example sentence',
    icon: 'pi pi-align-left',
    defaultOptions: exampleSentenceDefaults,
    revealsReading: false,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  confusableReadings: {
    component: CardBlockConfusableReadings,
    label: 'Confusable readings',
    icon: 'pi pi-exclamation-triangle',
    defaultOptions: confusableReadingsDefaults,
    revealsReading: false,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  frequencyRank: {
    component: CardBlockFrequencyRank,
    label: 'Frequency rank',
    icon: 'pi pi-chart-bar',
    defaultOptions: frequencyRankDefaults,
    revealsReading: false,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  etymology: {
    component: CardBlockEtymology,
    label: 'Etymology',
    icon: 'pi pi-globe',
    defaultOptions: etymologyDefaults,
    revealsReading: false,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  definitions: {
    component: CardBlockDefinitions,
    label: 'Definitions',
    icon: 'pi pi-book',
    defaultOptions: definitionsDefaults,
    revealsReading: false,
    revealsMeaning: true,
    revealsAnswer: true,
  },
  customMeaning: {
    component: CardBlockCustomMeaning,
    label: 'Custom notes',
    icon: 'pi pi-pencil',
    defaultOptions: customMeaningDefaults,
    revealsReading: false,
    revealsMeaning: true,
    revealsAnswer: true,
  },
  pitchAccent: {
    component: CardBlockPitchAccent,
    label: 'Pitch accent',
    icon: 'pi pi-chart-line',
    defaultOptions: pitchAccentDefaults,
    revealsReading: true,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  kanjiBreakdown: {
    component: CardBlockKanjiBreakdown,
    label: 'Kanji breakdown',
    icon: 'pi pi-th-large',
    defaultOptions: kanjiBreakdownDefaults,
    revealsReading: false,
    revealsMeaning: true,
    revealsAnswer: true,
  },
  wordComposition: {
    component: CardBlockWordComposition,
    label: 'Word composition',
    icon: 'pi pi-sitemap',
    defaultOptions: wordCompositionDefaults,
    revealsReading: false,
    revealsMeaning: true,
    revealsAnswer: true,
  },
  wordUsedIn: {
    component: CardBlockWordUsedIn,
    label: 'Used in',
    icon: 'pi pi-link',
    defaultOptions: wordUsedInDefaults,
    revealsReading: false,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  deckOccurrences: {
    component: CardBlockDeckOccurrences,
    label: 'Deck occurrences',
    icon: 'pi pi-list',
    defaultOptions: deckOccurrencesDefaults,
    revealsReading: false,
    revealsMeaning: false,
    revealsAnswer: false,
  },
  divider: {
    component: CardBlockDivider,
    label: 'Divider',
    icon: 'pi pi-minus',
    defaultOptions: dividerDefaults,
    revealsReading: false,
    revealsMeaning: false,
    revealsAnswer: false,
  },
};
