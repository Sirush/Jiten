import { computed, ref, type ComputedRef, type Ref } from 'vue';
import type { StudySettingsDto } from '~/types';
import { resolveCardLayout } from '~/utils/cardLayout';
import type { CardContext, CardSampleData } from './useCardContext';

// Self-contained placeholder so the card-image block renders something in the preview and editor rows
// without loading a real (Jiten+) upload. Inline SVG keeps it CSP-safe — no external URL.
const SAMPLE_IMAGE_SVG = `<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200" viewBox="0 0 320 200">
  <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#93c5fd"/><stop offset="1" stop-color="#c4b5fd"/>
  </linearGradient></defs>
  <rect width="320" height="200" rx="12" fill="url(#g)"/>
  <circle cx="248" cy="52" r="26" fill="#fde68a"/>
  <path d="M0 200 L96 96 L160 160 L216 104 L320 200 Z" fill="#4b5563" opacity="0.85"/>
  <path d="M0 200 L64 132 L128 200 Z" fill="#374151" opacity="0.7"/>
  <text x="160" y="188" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#f9fafb" opacity="0.9">Sample image</text>
</svg>`;
export const SAMPLE_CARD_IMAGE = `data:image/svg+xml,${encodeURIComponent(SAMPLE_IMAGE_SVG)}`;

export const SAMPLE_CARD: CardSampleData = {
  isNew: true,
  wordRuby: '事[じ]典[てん]',
  wordPlain: '事典',
  reading: 'じてん',
  pitchAccent: 0,
  frequencyRank: 55048,
  pos: 'n',
  definitions: ['encyclopedia', 'Example definition 2', 'Example definition 3', 'Example definition 4', 'Example definition 5'],
  confusable: ['ことてん'],
  kanji: [
    { character: '事', strokeCount: 8, meaning: 'matter', jlpt: 3 },
    { character: '典', strokeCount: 8, meaning: 'code', jlpt: 1 },
  ],
  composedOf: [
    { ruby: '事[こと]', def: 'thing, matter' },
    { ruby: '典[てん]', def: 'law code' },
  ],
  usedIn: [
    { ruby: '百[ひゃっ]科[か]事[じ]典[てん]', rank: 46030, def: 'encyclopedia' },
    { ruby: '世[せ]界[かい]大[だい]百[ひゃっ]科[か]事[じ]典[てん]', rank: 242669, def: 'Heibonsha World Encyclopedia' },
  ],
  usedInTotal: 2,
  example: { text: 'わからない言葉は事典で調べる。', word: '事典', source: 'Steins;Gate (Visual Novel)' },
  // Illustrative only — the real 事典 has no foreign etymology.
  languageSources: [{ lang: 'chi', text: '', isWasei: false, isPartial: false }],
  deckOccurrences: [
    { deckId: 1, originalTitle: 'Steins;Gate', occurrences: 12 },
    { deckId: 2, originalTitle: '化物語', romajiTitle: 'Bakemonogatari', occurrences: 7 },
    { deckId: 3, originalTitle: 'よつばと！', romajiTitle: 'Yotsuba to!', occurrences: 3 },
  ],
  image: SAMPLE_CARD_IMAGE,
  customMeaning: 'the 事 one — an encyclopedia, not 辞典 (a dictionary)',
};

/**
 * A non-interactive {@link CardContext} backed by {@link SAMPLE_CARD}, for rendering the real card
 * block components in the settings preview and the layout editor where no live card or network exists.
 * `isFlipped` is passed in so a caller can pin a panel to the front or back view.
 *
 * `isolated` is set for the editor's per-block rows, which render a single block out of card context:
 * there the image always renders below (no beside/mobile split) and is never blurred, so a beside-layout
 * card-image row still shows its placeholder on wide screens.
 */
export function createSampleCardContext(
  settings: ComputedRef<StudySettingsDto>,
  isFlipped: Ref<boolean>,
  opts: { isolated?: boolean } = {}
): CardContext {
  const exampleRevealed = ref(false);
  const isolated = !!opts.isolated;

  const sampleLayout = computed(() => resolveCardLayout(settings.value));
  const imageBlock = computed(() => sampleLayout.value.front.find((b) => b.type === 'cardImage') ?? sampleLayout.value.back.find((b) => b.type === 'cardImage'));
  const imageOnFront = computed(() => sampleLayout.value.front.some((b) => b.type === 'cardImage'));
  const besideLayout = computed(() => !isolated && (imageBlock.value?.options?.layout ?? 'beside') === 'beside');
  const blurEnabled = computed(() => (imageBlock.value?.options?.blur ?? true));

  return {
    card: computed(() => null),
    settings,
    isFlipped,
    isPreview: true,
    sample: SAMPLE_CARD,
    wordData: ref(null),
    wordLoading: ref(false),
    wordLoadFailed: ref(false),
    writeInActive: ref(false),
    writeInFrontFurigana: ref('default'),
    writeInOutcome: ref(null),
    writeInInputPhase: ref(false),
    cardImage: computed(() =>
      imageBlock.value
        ? { kind: 'image', url: SAMPLE_CARD_IMAGE, contentType: 'image/svg+xml', fileSizeBytes: 0, createdAt: '', inherited: false, sourceReadingIndex: 0 }
        : null
    ),
    cardImageUrl: computed(() => (imageBlock.value ? SAMPLE_CARD_IMAGE : '')),
    cardAudio: computed(() => null),
    customAudioPlaying: ref(false),
    imageBlurred: computed(() => !isolated && imageOnFront.value && blurEnabled.value && !isFlipped.value),
    showBesideImage: computed(() => !!imageBlock.value && besideLayout.value && (imageOnFront.value || isFlipped.value)),
    imageBesideLayout: besideLayout,
    headWordTtsText: computed(() => ''),
    playCustomAudio: () => {},
    onImageError: () => {},
    revealImage: () => {},
    cardExample: computed(() => null),
    exampleRevealed,
    revealExample: () => {
      exampleRevealed.value = true;
    },
    registerDictCycler: () => {},
  };
}
