import { computed, onMounted, ref, type ComputedRef, type Ref } from 'vue';
import type { StudySettingsDto } from '~/types';
import { resolveCardLayout } from '~/utils/cardLayout';
import type { CardContext, CardSampleData } from './useCardContext';

// Self-contained placeholder so the card-image block renders something in the preview and editor rows
// without loading a real (Jiten+) upload. Inline SVG keeps it CSP-safe — no external URL.
const sampleImageSvg = (extra = '') => `<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200" viewBox="0 0 320 200">
  <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#93c5fd"/><stop offset="1" stop-color="#c4b5fd"/>
  </linearGradient></defs>
  <rect width="320" height="200" rx="12" fill="url(#g)"/>
  <circle cx="248" cy="52" r="26" fill="#fde68a"/>
  <path d="M0 200 L96 96 L160 160 L216 104 L320 200 Z" fill="#4b5563" opacity="0.85"/>
  <path d="M0 200 L64 132 L128 200 Z" fill="#374151" opacity="0.7"/>
  ${extra}
  <text x="160" y="188" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#f9fafb" opacity="0.9">Sample image</text>
</svg>`;

export const SAMPLE_CARD_IMAGE = `data:image/svg+xml,${encodeURIComponent(sampleImageSvg())}`;

const SAMPLE_IMAGE_SEED =
  'PGcgdHJhbnNmb3JtPSJ0cmFuc2xhdGUoNjUgLTE4KSI+IDxnIGZpbGw9IiNmY2QzNGQiPiA8ZWxsaXBzZSBjeD0iNzIiIGN5PSIxMDgiIHJ4PSIxMCIgcnk9IjciLz4gPGVsbGlwc2UgY3g9IjcyIiBjeT0iMTE5IiByeD0iOC41IiByeT0iNi41Ii8+IDxlbGxpcHNlIGN4PSI3MiIgY3k9IjEyOSIgcng9IjciIHJ5PSI2Ii8+IDxlbGxpcHNlIGN4PSI3MiIgY3k9IjEzOCIgcng9IjUuNSIgcnk9IjUiLz4gPHBhdGggZD0iTTY3IDE0MCBRNzIgMTU4IDc1IDE0MCBaIi8+IDxlbGxpcHNlIGN4PSIxMTgiIGN5PSIxMDgiIHJ4PSIxMCIgcnk9IjciLz4gPGVsbGlwc2UgY3g9IjExOCIgY3k9IjExOSIgcng9IjguNSIgcnk9IjYuNSIvPiA8ZWxsaXBzZSBjeD0iMTE4IiBjeT0iMTI5IiByeD0iNyIgcnk9IjYiLz4gPGVsbGlwc2UgY3g9IjExOCIgY3k9IjEzOCIgcng9IjUuNSIgcnk9IjUiLz4gPHBhdGggZD0iTTExMyAxNDAgUTExOCAxNTggMTIxIDE0MCBaIi8+IDwvZz4gPGcgc3Ryb2tlPSIjZDk3NzA2IiBzdHJva2Utd2lkdGg9IjEuMyIgZmlsbD0ibm9uZSIgc3Ryb2tlLWxpbmVjYXA9InJvdW5kIj4gPHBhdGggZD0iTTYyLjUgMTEwIFE3MiAxMTYgODEuNSAxMDgiLz4gPHBhdGggZD0iTTY0IDEyMSBRNzIgMTI3IDgwIDExOSIvPiA8cGF0aCBkPSJNNjUuNSAxMzEgUTcyIDEzNiA3OC41IDEyOSIvPiA8cGF0aCBkPSJNNjcgMTM5IFE3MiAxNDQgNzcgMTM3Ii8+IDxwYXRoIGQ9Ik0xMDguNSAxMTAgUTExOCAxMTYgMTI3LjUgMTA4Ii8+IDxwYXRoIGQ9Ik0xMTAgMTIxIFExMTggMTI3IDEyNiAxMTkiLz4gPHBhdGggZD0iTTExMS41IDEzMSBRMTE4IDEzNiAxMjQuNSAxMjkiLz4gPHBhdGggZD0iTTExMyAxMzkgUTExOCAxNDQgMTIzIDEzNyIvPiA8L2c+IDxyZWN0IHg9Ijg4IiB5PSIxNjQiIHdpZHRoPSI1IiBoZWlnaHQ9IjE4IiByeD0iMi41IiBmaWxsPSIjZmNkOWJkIi8+IDxyZWN0IHg9Ijk3IiB5PSIxNjQiIHdpZHRoPSI1IiBoZWlnaHQ9IjE4IiByeD0iMi41IiBmaWxsPSIjZmNkOWJkIi8+IDxwYXRoIGQ9Ik04NyAxMjAgTDEwMyAxMjAgTDExMiAxNjYgUTk1IDE3MyA3OCAxNjYgWiIgZmlsbD0iI2RjMjYyNiIvPiA8cGF0aCBkPSJNNzggMTY2IFE4MS40IDE3Mi41IDg0LjggMTY4IFE4OC4yIDE3NCA5MS42IDE2OS41IFE5NSAxNzUuNSA5OC40IDE2OS41IFExMDEuOCAxNzQgMTA1LjIgMTY4IFExMDguNiAxNzIuNSAxMTIgMTY2IFE5NSAxNzMgNzggMTY2IFoiIGZpbGw9IiNmZWYyZjIiLz4gPHBhdGggZD0iTTgzLjkgMTM1IEwxMDYuMSAxMzUgTDEwNi42IDEzOSBMODMuNCAxMzkgWiIgZmlsbD0iI2I5MWMxYyIvPiA8bGluZSB4MT0iOTAiIHkxPSIxNDEiIHgyPSI4Ny41IiB5Mj0iMTYzIiBzdHJva2U9IiNiOTFjMWMiIHN0cm9rZS13aWR0aD0iMS4yIiBzdHJva2UtbGluZWNhcD0icm91bmQiLz4gPGxpbmUgeDE9IjEwMCIgeTE9IjE0MSIgeDI9IjEwMi41IiB5Mj0iMTYzIiBzdHJva2U9IiNiOTFjMWMiIHN0cm9rZS13aWR0aD0iMS4yIiBzdHJva2UtbGluZWNhcD0icm91bmQiLz4gPHBhdGggZD0iTTg3IDEyMCBRODkuNyAxMjMuNSA5Mi4zIDEyMC41IFE5NSAxMjQgOTcuNyAxMjAuNSBRMTAwLjMgMTIzLjUgMTAzIDEyMCBaIiBmaWxsPSIjZmVmMmYyIi8+IDxsaW5lIHgxPSI4NSIgeTE9IjEzMiIgeDI9Ijc2IiB5Mj0iMTQ2IiBzdHJva2U9IiNmY2Q5YmQiIHN0cm9rZS13aWR0aD0iNSIgc3Ryb2tlLWxpbmVjYXA9InJvdW5kIi8+IDxsaW5lIHgxPSIxMDUiIHkxPSIxMzIiIHgyPSIxMTQiIHkyPSIxNDYiIHN0cm9rZT0iI2ZjZDliZCIgc3Ryb2tlLXdpZHRoPSI1IiBzdHJva2UtbGluZWNhcD0icm91bmQiLz4gPGNpcmNsZSBjeD0iOTUiIGN5PSIxMDQiIHI9IjE2IiBmaWxsPSIjZmNkOWJkIi8+IDxwYXRoIGQ9Ik03OSAxMDQgQTE2IDE2IDAgMCAxIDExMSAxMDQgTDEwNSA5OSBMOTkgMTAzIEw5NSA5OCBMOTEgMTAzIEw4NSA5OSBaIiBmaWxsPSIjZmNkMzRkIi8+IDxwYXRoIGQ9Ik05NSA4NyBROTkgODAgMTA1IDgyIFE5OCA4MiA5NyA4OCBaIiBmaWxsPSIjZmNkMzRkIi8+IDxjaXJjbGUgY3g9Ijg5IiBjeT0iMTA4IiByPSIyLjIiIGZpbGw9IiMxZjI5MzciLz4gPGNpcmNsZSBjeD0iMTAxIiBjeT0iMTA4IiByPSIyLjIiIGZpbGw9IiMxZjI5MzciLz4gPHBhdGggZD0iTTkxIDExMy41IFE5MyAxMTYuNSA5NSAxMTMuNSBROTcgMTE2LjUgOTkgMTEzLjUiIHN0cm9rZT0iIzFmMjkzNyIgc3Ryb2tlLXdpZHRoPSIxLjIiIGZpbGw9Im5vbmUiIHN0cm9rZS1saW5lY2FwPSJyb3VuZCIvPiA8L2c+';

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

  // Resolved on the client after mount so SSR markup stays deterministic.
  const sampleImage = ref(SAMPLE_CARD_IMAGE);
  onMounted(() => {
    if (Math.floor(Math.random() * 1000) === 210) sampleImage.value = `data:image/svg+xml,${encodeURIComponent(sampleImageSvg(atob(SAMPLE_IMAGE_SEED)))}`;
  });

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
        ? { kind: 'image', url: sampleImage.value, contentType: 'image/svg+xml', fileSizeBytes: 0, createdAt: '', inherited: false, sourceReadingIndex: 0 }
        : null
    ),
    cardImageUrl: computed(() => (imageBlock.value ? sampleImage.value : '')),
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
