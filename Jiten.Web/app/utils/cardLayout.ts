import type { CardBlockOptions, CardBlockType, CardLayout, CardLayoutBlock, HeadwordFurigana, StudySettingsDto } from '~/types';

export type LayoutSide = 'front' | 'back';

// Runtime list of every block type. Mirrors the CardBlockType union and the registry keys, but lives
// here (a component-free module) so pure utilities can validate imported layouts without pulling the
// Vue-backed registry into their import graph.
export const ALL_CARD_BLOCK_TYPES: readonly CardBlockType[] = [
  'cardStatus',
  'headword',
  'cardImage',
  'exampleSentence',
  'confusableReadings',
  'frequencyRank',
  'etymology',
  'definitions',
  'customMeaning',
  'pitchAccent',
  'kanjiBreakdown',
  'wordComposition',
  'wordUsedIn',
  'deckOccurrences',
  'divider',
];

let idCounter = 0;

export function newBlockId(): string {
  idCounter = (idCounter + 1) % 0xffffff;
  return `${Date.now().toString(36)}-${idCounter.toString(36)}-${Math.floor(Math.random() * 0xffffff).toString(36)}`;
}

function block(type: CardBlockType, options?: CardLayoutBlock['options']): CardLayoutBlock {
  return options ? { id: newBlockId(), type, options } : { id: newBlockId(), type };
}

// The cardImage block defaults are layout='beside', blur=true, so a default-valued image stores no
// options and round-trips identically to a legacy-derived one.
function cardImageOptionsFromLegacy(s: StudySettingsDto): CardBlockOptions | undefined {
  const options: CardBlockOptions = {};
  if (s.cardImageLayout === 'below') options.layout = 'below';
  if (s.blurCardImage === false) options.blur = false;
  return Object.keys(options).length ? options : undefined;
}

/**
 * Derives a {@link CardLayout} from the legacy per-card display toggles. This is the source of truth
 * for the card's block order until the layout editor writes an explicit `cardLayout`. The `Default`
 * preset is this function applied to the default toggle values.
 *
 * Blocks that self-hide when their data is absent (etymology, definitions, customMeaning,
 * deckOccurrences) are always included; the toggle-gated blocks are only emitted when enabled.
 */
export function buildLayoutFromLegacySettings(s: StudySettingsDto): CardLayout {
  const front: CardLayoutBlock[] = [];
  const back: CardLayoutBlock[] = [];

  if (s.showCardStatus) front.push(block('cardStatus'));

  const furigana: HeadwordFurigana = !s.showFuriganaOnFront ? 'afterFlip' : s.furiganaOnFrontNewOnly ? 'newOnly' : 'shown';
  front.push(block('headword', furigana === 'afterFlip' ? undefined : { furigana }));

  // The image block is always emitted (it self-hides when the card has no uploaded media); its list
  // decides the side and its options decide beside/below and blur.
  const imageOnFront = s.cardImagePosition === 'Front';
  const imageOptions = cardImageOptionsFromLegacy(s);
  if (imageOnFront) front.push(block('cardImage', imageOptions));

  if (s.exampleSentencePosition === 'Front') {
    front.push(block('exampleSentence', s.blurExampleSentence ? { blur: true } : undefined));
  }

  if (s.showConfusableReadings) front.push(block('confusableReadings'));

  if (s.showFrequencyRank) back.push(block('frequencyRank'));
  if (!imageOnFront) back.push(block('cardImage', imageOptions));
  back.push(block('etymology'));
  back.push(block('definitions'));
  back.push(block('customMeaning'));
  if (s.exampleSentencePosition === 'Back') {
    back.push(block('exampleSentence', s.blurExampleSentence ? { blur: true } : undefined));
  }
  if (s.showPitchAccent) back.push(block('pitchAccent'));
  if (s.showKanjiBreakdown) back.push(block('kanjiBreakdown'));
  if (s.showWordComposition) back.push(block('wordComposition'));
  if (s.showWordUsedIn) back.push(block('wordUsedIn'));
  back.push(block('deckOccurrences'));

  return { version: 1, front, back };
}

/**
 * The card layout that actually drives the display. Precedence: an explicit `cardLayout` wins and the
 * legacy toggles are ignored; only when it is null/absent is the layout derived from those toggles.
 */
export function resolveCardLayout(s: StudySettingsDto): CardLayout {
  return s.cardLayout ?? buildLayoutFromLegacySettings(s);
}

/**
 * Moves one block between (or within) the two side lists, returning fresh arrays. `to.index` is the
 * insertion position in the destination list once the moved block has been removed — matching the drop
 * index produced by {@link useTouchReorderMulti}, which excludes the dragged element. Returns the inputs
 * unchanged when the source index is out of range.
 */
export function moveBlock(
  front: CardLayoutBlock[],
  back: CardLayoutBlock[],
  from: { list: LayoutSide; index: number },
  to: { list: LayoutSide; index: number }
): { front: CardLayoutBlock[]; back: CardLayoutBlock[] } {
  const lists: Record<LayoutSide, CardLayoutBlock[]> = { front: [...front], back: [...back] };
  const src = lists[from.list];
  if (from.index < 0 || from.index >= src.length) return { front, back };
  const [moved] = src.splice(from.index, 1);
  const dst = lists[to.list];
  const insert = Math.max(0, Math.min(to.index, dst.length));
  dst.splice(insert, 0, moved);
  return { front: lists.front, back: lists.back };
}

// The canonical block order per side, mirroring what buildLayoutFromLegacySettings emits. It drives
// where a simple toggle re-inserts a block and how hasCustomArrangement judges "still canonical".
const CANONICAL_FRONT: CardBlockType[] = ['cardStatus', 'headword', 'cardImage', 'exampleSentence', 'confusableReadings'];
const CANONICAL_BACK: CardBlockType[] = [
  'frequencyRank',
  'cardImage',
  'etymology',
  'definitions',
  'customMeaning',
  'exampleSentence',
  'pitchAccent',
  'kanjiBreakdown',
  'wordComposition',
  'wordUsedIn',
  'deckOccurrences',
];
// The side a block belongs on when a toggle adds it. exampleSentence and cardImage are intentionally
// absent — they are canonical on either side; divider is absent — it has no canonical home.
const CANONICAL_SIDE: Partial<Record<CardBlockType, LayoutSide>> = {
  cardStatus: 'front',
  headword: 'front',
  confusableReadings: 'front',
  frequencyRank: 'back',
  etymology: 'back',
  definitions: 'back',
  customMeaning: 'back',
  pitchAccent: 'back',
  kanjiBreakdown: 'back',
  wordComposition: 'back',
  wordUsedIn: 'back',
  deckOccurrences: 'back',
};

function insertBlockAtCanonical(list: CardLayoutBlock[], blk: CardLayoutBlock, side: LayoutSide): CardLayoutBlock[] {
  const seq = side === 'front' ? CANONICAL_FRONT : CANONICAL_BACK;
  const k = seq.indexOf(blk.type);
  let insertAt = 0;
  list.forEach((b, i) => {
    const bi = seq.indexOf(b.type);
    if (bi !== -1 && bi < k) insertAt = i + 1;
  });
  const next = [...list];
  next.splice(insertAt, 0, blk);
  return next;
}

export function layoutHasBlock(layout: CardLayout, type: CardBlockType): boolean {
  return layout.front.some((b) => b.type === type) || layout.back.some((b) => b.type === type);
}

export function setBlockPresence(layout: CardLayout, type: CardBlockType, present: boolean): CardLayout {
  if (!present) {
    return {
      version: 1,
      front: layout.front.filter((b) => b.type !== type),
      back: layout.back.filter((b) => b.type !== type),
    };
  }
  if (layoutHasBlock(layout, type)) return { version: 1, front: [...layout.front], back: [...layout.back] };
  const side = CANONICAL_SIDE[type] ?? 'back';
  return {
    version: 1,
    front: side === 'front' ? insertBlockAtCanonical(layout.front, block(type), 'front') : [...layout.front],
    back: side === 'back' ? insertBlockAtCanonical(layout.back, block(type), 'back') : [...layout.back],
  };
}

function withoutOptionKey(b: CardLayoutBlock, key: keyof CardBlockOptions): CardLayoutBlock {
  if (!b.options || !(key in b.options)) return b;
  const rest = Object.fromEntries(Object.entries(b.options).filter(([k]) => k !== key)) as CardBlockOptions;
  return Object.keys(rest).length ? { ...b, options: rest } : { id: b.id, type: b.type };
}

export function setHeadwordFurigana(layout: CardLayout, furigana: HeadwordFurigana): CardLayout {
  const map = (b: CardLayoutBlock): CardLayoutBlock => {
    if (b.type !== 'headword') return b;
    // afterFlip is the default; store nothing rather than an explicit option so the block round-trips
    // identically to a legacy-derived one.
    if (furigana === 'afterFlip') return withoutOptionKey(b, 'furigana');
    return { ...b, options: { ...b.options, furigana } };
  };
  return { version: 1, front: layout.front.map(map), back: layout.back.map(map) };
}

export function getHeadwordFurigana(layout: CardLayout): HeadwordFurigana {
  const hw = [...layout.front, ...layout.back].find((b) => b.type === 'headword');
  return hw?.options?.furigana ?? 'afterFlip';
}

export function getSentencePosition(layout: CardLayout): 'Hidden' | 'Front' | 'Back' {
  if (layout.front.some((b) => b.type === 'exampleSentence')) return 'Front';
  if (layout.back.some((b) => b.type === 'exampleSentence')) return 'Back';
  return 'Hidden';
}

export function setSentencePosition(layout: CardLayout, pos: 'Hidden' | 'Front' | 'Back', fallbackBlur: boolean): CardLayout {
  const existing = [...layout.front, ...layout.back].find((b) => b.type === 'exampleSentence');
  const front = layout.front.filter((b) => b.type !== 'exampleSentence');
  const back = layout.back.filter((b) => b.type !== 'exampleSentence');
  if (pos === 'Hidden') return { version: 1, front, back };
  const options = existing?.options ? { ...existing.options } : fallbackBlur ? { blur: true } : undefined;
  const blk = block('exampleSentence', options);
  return pos === 'Front'
    ? { version: 1, front: insertBlockAtCanonical(front, blk, 'front'), back }
    : { version: 1, front, back: insertBlockAtCanonical(back, blk, 'back') };
}

export function setSentenceBlur(layout: CardLayout, blur: boolean): CardLayout {
  const map = (b: CardLayoutBlock): CardLayoutBlock => {
    if (b.type !== 'exampleSentence') return b;
    return blur ? { ...b, options: { ...b.options, blur: true } } : withoutOptionKey(b, 'blur');
  };
  return { version: 1, front: layout.front.map(map), back: layout.back.map(map) };
}

export function getCardImageBlock(layout: CardLayout): CardLayoutBlock | undefined {
  return layout.front.find((b) => b.type === 'cardImage') ?? layout.back.find((b) => b.type === 'cardImage');
}

/** Which side the image is on, i.e. whether it is visible before the flip. */
export function getCardImagePosition(layout: CardLayout): 'Front' | 'Back' {
  return layout.front.some((b) => b.type === 'cardImage') ? 'Front' : 'Back';
}

// Moves the image block to the requested side (removing any duplicates), preserving its options and
// re-creating it when the layout has none.
export function setCardImagePosition(layout: CardLayout, pos: 'Front' | 'Back'): CardLayout {
  const existing = getCardImageBlock(layout);
  const front = layout.front.filter((b) => b.type !== 'cardImage');
  const back = layout.back.filter((b) => b.type !== 'cardImage');
  const blk = block('cardImage', existing?.options ? { ...existing.options } : undefined);
  return pos === 'Front'
    ? { version: 1, front: insertBlockAtCanonical(front, blk, 'front'), back }
    : { version: 1, front, back: insertBlockAtCanonical(back, blk, 'back') };
}

// Sets one image option across the (single) image block, re-adding it on the back when absent. A
// default value (beside / blur=true) clears the key so the block round-trips like a legacy-derived one.
export function setCardImageOption(layout: CardLayout, key: 'layout' | 'blur', value: 'beside' | 'below' | boolean): CardLayout {
  if (!getCardImageBlock(layout)) return setCardImageOption(setCardImagePosition(layout, 'Back'), key, value);
  const isDefault = (key === 'layout' && value === 'beside') || (key === 'blur' && value === true);
  const map = (b: CardLayoutBlock): CardLayoutBlock => {
    if (b.type !== 'cardImage') return b;
    if (isDefault) return withoutOptionKey(b, key);
    return { ...b, options: { ...b.options, [key]: value } as CardBlockOptions };
  };
  return { version: 1, front: layout.front.map(map), back: layout.back.map(map) };
}

export function hasCustomArrangement(layout: CardLayout): boolean {
  const all = [...layout.front, ...layout.back];
  if (all.some((b) => b.type === 'divider')) return true;

  const counts = new Map<CardBlockType, number>();
  for (const b of all) counts.set(b.type, (counts.get(b.type) ?? 0) + 1);
  for (const c of counts.values()) if (c > 1) return true;

  const sides: LayoutSide[] = ['front', 'back'];
  for (const side of sides) {
    const list = side === 'front' ? layout.front : layout.back;
    const seq = side === 'front' ? CANONICAL_FRONT : CANONICAL_BACK;
    for (const b of list) {
      const canon = CANONICAL_SIDE[b.type];
      if (canon && canon !== side) return true;
    }
    // frequencyRank is top-bar chrome: the editor re-appends it at the end of the list, and its list
    // position never affects the rendered card, so it is excluded from the order comparison.
    const present = list.map((b) => b.type).filter((t) => t !== 'frequencyRank' && seq.includes(t));
    const canonicalOrder = seq.filter((t) => present.includes(t));
    if (present.join(',') !== canonicalOrder.join(',')) return true;
  }
  return false;
}
