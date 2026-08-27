import { describe, expect, it } from 'vitest';
import {
  buildLayoutFromLegacySettings,
  getCardImageBlock,
  getCardImagePosition,
  getHeadwordFurigana,
  getSentencePosition,
  hasCustomArrangement,
  layoutHasBlock,
  setBlockPresence,
  setCardImageOption,
  setCardImagePosition,
  setHeadwordFurigana,
  setSentenceBlur,
  setSentencePosition,
} from '../app/utils/cardLayout';
import type { CardBlockOptions, CardBlockType, CardLayout, CardLayoutBlock, StudySettingsDto } from '../app/types/types';

// The builder only reads the display toggles below; the rest of StudySettingsDto is irrelevant here.
function settings(overrides: Partial<StudySettingsDto> = {}): StudySettingsDto {
  return {
    showCardStatus: false,
    showFuriganaOnFront: false,
    furiganaOnFrontNewOnly: false,
    exampleSentencePosition: 'Hidden',
    blurExampleSentence: false,
    showConfusableReadings: false,
    showFrequencyRank: false,
    showPitchAccent: false,
    showKanjiBreakdown: false,
    showWordComposition: false,
    showWordUsedIn: false,
    ...overrides,
  } as StudySettingsDto;
}

const types = (blocks: { type: CardBlockType }[]) => blocks.map((b) => b.type);

describe('buildLayoutFromLegacySettings', () => {
  it('produces the minimal default layout with everything off', () => {
    const layout = buildLayoutFromLegacySettings(settings());
    expect(layout.version).toBe(1);
    expect(types(layout.front)).toEqual(['headword']);
    // The image block is always emitted; with no explicit position it lands on the back.
    expect(types(layout.back)).toEqual(['cardImage', 'etymology', 'definitions', 'customMeaning', 'deckOccurrences']);
  });

  it('assigns a unique id to every block', () => {
    const layout = buildLayoutFromLegacySettings(settings({ showFrequencyRank: true, showPitchAccent: true }));
    const ids = [...layout.front, ...layout.back].map((b) => b.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('prepends the card status block on the front when enabled', () => {
    const layout = buildLayoutFromLegacySettings(settings({ showCardStatus: true }));
    expect(types(layout.front)).toEqual(['cardStatus', 'headword']);
  });

  it('leaves the headword furigana at the default (afterFlip) with no options when front furigana is off', () => {
    const layout = buildLayoutFromLegacySettings(settings());
    const headword = layout.front.find((b) => b.type === 'headword')!;
    expect(headword.options).toBeUndefined();
  });

  it('maps showFuriganaOnFront to the shown furigana option', () => {
    const layout = buildLayoutFromLegacySettings(settings({ showFuriganaOnFront: true }));
    const headword = layout.front.find((b) => b.type === 'headword')!;
    expect(headword.options).toEqual({ furigana: 'shown' });
  });

  it('maps furiganaOnFrontNewOnly to the newOnly furigana option', () => {
    const layout = buildLayoutFromLegacySettings(settings({ showFuriganaOnFront: true, furiganaOnFrontNewOnly: true }));
    const headword = layout.front.find((b) => b.type === 'headword')!;
    expect(headword.options).toEqual({ furigana: 'newOnly' });
  });

  it('places the example sentence on the front after the headword', () => {
    const layout = buildLayoutFromLegacySettings(settings({ exampleSentencePosition: 'Front' }));
    expect(types(layout.front)).toEqual(['headword', 'exampleSentence']);
    expect(types(layout.back)).not.toContain('exampleSentence');
    const example = layout.front.find((b) => b.type === 'exampleSentence')!;
    expect(example.options).toBeUndefined();
  });

  it('places the example sentence on the back after the custom meaning', () => {
    const layout = buildLayoutFromLegacySettings(settings({ exampleSentencePosition: 'Back' }));
    expect(types(layout.front)).not.toContain('exampleSentence');
    expect(types(layout.back)).toEqual(['cardImage', 'etymology', 'definitions', 'customMeaning', 'exampleSentence', 'deckOccurrences']);
  });

  it('carries the blur option onto the example sentence when blur is enabled', () => {
    const front = buildLayoutFromLegacySettings(settings({ exampleSentencePosition: 'Front', blurExampleSentence: true }));
    expect(front.front.find((b) => b.type === 'exampleSentence')!.options).toEqual({ blur: true });
    const back = buildLayoutFromLegacySettings(settings({ exampleSentencePosition: 'Back', blurExampleSentence: true }));
    expect(back.back.find((b) => b.type === 'exampleSentence')!.options).toEqual({ blur: true });
  });

  it('omits the example sentence entirely when hidden', () => {
    const layout = buildLayoutFromLegacySettings(settings({ exampleSentencePosition: 'Hidden' }));
    expect(types(layout.front)).not.toContain('exampleSentence');
    expect(types(layout.back)).not.toContain('exampleSentence');
  });

  it('adds the confusable readings block to the front when enabled', () => {
    const layout = buildLayoutFromLegacySettings(settings({ showConfusableReadings: true }));
    expect(types(layout.front)).toEqual(['headword', 'confusableReadings']);
  });

  it('adds the frequency rank block to the top of the back when enabled', () => {
    const layout = buildLayoutFromLegacySettings(settings({ showFrequencyRank: true }));
    expect(layout.back[0].type).toBe('frequencyRank');
  });

  it('appends each optional back block in the canonical order', () => {
    const layout = buildLayoutFromLegacySettings(
      settings({ showPitchAccent: true, showKanjiBreakdown: true, showWordComposition: true, showWordUsedIn: true })
    );
    expect(types(layout.back)).toEqual([
      'cardImage',
      'etymology',
      'definitions',
      'customMeaning',
      'pitchAccent',
      'kanjiBreakdown',
      'wordComposition',
      'wordUsedIn',
      'deckOccurrences',
    ]);
  });

  it('reflects a fully-enabled configuration on both sides', () => {
    const layout = buildLayoutFromLegacySettings(
      settings({
        showCardStatus: true,
        showFuriganaOnFront: true,
        exampleSentencePosition: 'Back',
        blurExampleSentence: true,
        showConfusableReadings: true,
        showFrequencyRank: true,
        showPitchAccent: true,
        showKanjiBreakdown: true,
        showWordComposition: true,
        showWordUsedIn: true,
      })
    );
    expect(types(layout.front)).toEqual(['cardStatus', 'headword', 'confusableReadings']);
    expect(types(layout.back)).toEqual([
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
    ]);
  });

  it('emits the image block on the front right after the headword when positioned Front', () => {
    const layout = buildLayoutFromLegacySettings(settings({ cardImagePosition: 'Front' } as Partial<StudySettingsDto>));
    expect(types(layout.front)).toEqual(['headword', 'cardImage']);
    expect(types(layout.back)).not.toContain('cardImage');
  });

  it('stores only non-default image options', () => {
    const defaulted = buildLayoutFromLegacySettings(settings({ cardImageLayout: 'beside', blurCardImage: true } as Partial<StudySettingsDto>));
    expect(defaulted.back.find((b) => b.type === 'cardImage')!.options).toBeUndefined();
    const custom = buildLayoutFromLegacySettings(settings({ cardImageLayout: 'below', blurCardImage: false } as Partial<StudySettingsDto>));
    expect(custom.back.find((b) => b.type === 'cardImage')!.options).toEqual({ layout: 'below', blur: false });
  });
});

let seq = 0;
function b(type: CardBlockType, options?: CardBlockOptions): CardLayoutBlock {
  seq += 1;
  return options ? { id: `b${seq}`, type, options } : { id: `b${seq}`, type };
}
function layout(front: CardLayoutBlock[], back: CardLayoutBlock[]): CardLayout {
  return { version: 1, front, back };
}

describe('layoutHasBlock', () => {
  it('finds a block on either side', () => {
    const l = layout([b('headword')], [b('definitions')]);
    expect(layoutHasBlock(l, 'headword')).toBe(true);
    expect(layoutHasBlock(l, 'definitions')).toBe(true);
    expect(layoutHasBlock(l, 'pitchAccent')).toBe(false);
  });
});

describe('setBlockPresence', () => {
  const defaultBack = () => [b('etymology'), b('definitions'), b('customMeaning'), b('deckOccurrences')];

  it('inserts at the canonical middle position, after the last preceding canonical block', () => {
    const l = layout([b('headword')], defaultBack());
    const next = setBlockPresence(l, 'pitchAccent', true);
    expect(types(next.back)).toEqual(['etymology', 'definitions', 'customMeaning', 'pitchAccent', 'deckOccurrences']);
  });

  it('inserts at index 0 when no preceding canonical block anchors it', () => {
    const l = layout([b('headword')], defaultBack());
    const next = setBlockPresence(l, 'frequencyRank', true);
    expect(next.back[0]!.type).toBe('frequencyRank');
  });

  it('inserts a front block at its canonical position', () => {
    const l = layout([b('headword')], defaultBack());
    const next = setBlockPresence(l, 'cardStatus', true);
    expect(types(next.front)).toEqual(['cardStatus', 'headword']);
  });

  it('removes all instances of the type from both sides', () => {
    const l = layout([b('headword'), b('pitchAccent')], [b('pitchAccent'), b('definitions')]);
    const next = setBlockPresence(l, 'pitchAccent', false);
    expect(types(next.front)).toEqual(['headword']);
    expect(types(next.back)).toEqual(['definitions']);
  });

  it('is a no-op when the block is already present', () => {
    const l = layout([b('headword')], [b('pitchAccent')]);
    const next = setBlockPresence(l, 'pitchAccent', true);
    expect(types(next.back)).toEqual(['pitchAccent']);
  });

  it('does not mutate the input', () => {
    const back = defaultBack();
    const l = layout([b('headword')], back);
    setBlockPresence(l, 'pitchAccent', true);
    expect(types(back)).toEqual(['etymology', 'definitions', 'customMeaning', 'deckOccurrences']);
  });
});

describe('headword furigana helpers', () => {
  it('sets the option across every headword instance', () => {
    const l = layout([b('headword')], [b('headword'), b('definitions')]);
    const next = setHeadwordFurigana(l, 'newOnly');
    expect(next.front[0]!.options).toEqual({ furigana: 'newOnly' });
    expect(next.back[0]!.options).toEqual({ furigana: 'newOnly' });
    expect(getHeadwordFurigana(next)).toBe('newOnly');
  });

  it('clears the furigana key for afterFlip while preserving other options', () => {
    const l = layout([b('headword', { furigana: 'shown', showAudioButton: true })], []);
    const next = setHeadwordFurigana(l, 'afterFlip');
    expect(next.front[0]!.options).toEqual({ showAudioButton: true });
    expect(getHeadwordFurigana(next)).toBe('afterFlip');
  });

  it('drops the options object entirely when afterFlip leaves nothing behind', () => {
    const l = layout([b('headword', { furigana: 'shown' })], []);
    const next = setHeadwordFurigana(l, 'afterFlip');
    expect(next.front[0]!.options).toBeUndefined();
  });

  it('defaults to afterFlip when no headword block exists', () => {
    expect(getHeadwordFurigana(layout([], []))).toBe('afterFlip');
  });
});

describe('example sentence position helpers', () => {
  it('reports the front side when a sentence sits on both sides', () => {
    const l = layout([b('headword'), b('exampleSentence')], [b('definitions'), b('exampleSentence')]);
    expect(getSentencePosition(l)).toBe('Front');
  });

  it('moves the sentence to the back, preserving its options', () => {
    const l = layout([b('headword'), b('exampleSentence', { blur: true })], [b('definitions'), b('deckOccurrences')]);
    const next = setSentencePosition(l, 'Back', false);
    expect(types(next.front)).toEqual(['headword']);
    const es = next.back.find((x) => x.type === 'exampleSentence')!;
    expect(es.options).toEqual({ blur: true });
    // Canonical back order places it after definitions (no customMeaning present), before deckOccurrences.
    expect(types(next.back)).toEqual(['definitions', 'exampleSentence', 'deckOccurrences']);
  });

  it('applies the fallback blur when creating a fresh sentence block', () => {
    const l = layout([b('headword')], [b('definitions')]);
    const withBlur = setSentencePosition(l, 'Front', true);
    expect(withBlur.front.find((x) => x.type === 'exampleSentence')!.options).toEqual({ blur: true });
    const withoutBlur = setSentencePosition(l, 'Front', false);
    expect(withoutBlur.front.find((x) => x.type === 'exampleSentence')!.options).toBeUndefined();
  });

  it('removes every sentence block when hidden', () => {
    const l = layout([b('exampleSentence')], [b('exampleSentence'), b('definitions')]);
    const next = setSentencePosition(l, 'Hidden', false);
    expect(getSentencePosition(next)).toBe('Hidden');
    expect(types(next.back)).toEqual(['definitions']);
  });

  it('sets and clears blur on every sentence instance', () => {
    const l = layout([b('exampleSentence')], [b('exampleSentence', { blur: true, showSource: true })]);
    const on = setSentenceBlur(l, true);
    expect(on.front[0]!.options).toEqual({ blur: true });
    expect(on.back[0]!.options).toEqual({ blur: true, showSource: true });
    const off = setSentenceBlur(on, false);
    expect(off.front[0]!.options).toBeUndefined();
    expect(off.back[0]!.options).toEqual({ showSource: true });
  });
});

describe('card image helpers', () => {
  it('moves the image to the front at its canonical position, preserving options', () => {
    const l = layout([b('headword')], [b('cardImage', { layout: 'below', blur: false }), b('etymology')]);
    const next = setCardImagePosition(l, 'Front');
    expect(types(next.front)).toEqual(['headword', 'cardImage']);
    expect(types(next.back)).toEqual(['etymology']);
    expect(getCardImageBlock(next)!.options).toEqual({ layout: 'below', blur: false });
    expect(getCardImagePosition(next)).toBe('Front');
  });

  it('moves the image to the back and removes duplicates', () => {
    const l = layout([b('headword'), b('cardImage')], [b('cardImage'), b('etymology')]);
    const next = setCardImagePosition(l, 'Back');
    expect(types(next.front)).toEqual(['headword']);
    expect(next.back.filter((x) => x.type === 'cardImage')).toHaveLength(1);
    expect(getCardImagePosition(next)).toBe('Back');
  });

  it('re-adds the image on the back when a control changes while it is absent', () => {
    const l = layout([b('headword')], [b('etymology')]);
    expect(getCardImageBlock(l)).toBeUndefined();
    const next = setCardImageOption(l, 'layout', 'below');
    expect(getCardImageBlock(next)!.options).toEqual({ layout: 'below' });
    expect(getCardImagePosition(next)).toBe('Back');
  });

  it('clears an option back to its default value', () => {
    const l = layout([b('cardImage', { layout: 'below', blur: false })], [b('etymology')]);
    const beside = setCardImageOption(l, 'layout', 'beside');
    expect(getCardImageBlock(beside)!.options).toEqual({ blur: false });
    const unblurred = setCardImageOption(beside, 'blur', true);
    expect(getCardImageBlock(unblurred)!.options).toBeUndefined();
  });
});

describe('hasCustomArrangement', () => {
  it('is false for a default legacy-derived layout', () => {
    expect(hasCustomArrangement(buildLayoutFromLegacySettings(settings()))).toBe(false);
  });

  it('is not tripped by the image block on either canonical side', () => {
    expect(hasCustomArrangement(buildLayoutFromLegacySettings(settings({ cardImagePosition: 'Front' } as Partial<StudySettingsDto>)))).toBe(false);
    expect(hasCustomArrangement(buildLayoutFromLegacySettings(settings({ cardImagePosition: 'Back' } as Partial<StudySettingsDto>)))).toBe(false);
  });

  it('is true when the image block is duplicated', () => {
    expect(hasCustomArrangement(layout([b('headword'), b('cardImage')], [b('cardImage'), b('etymology')]))).toBe(true);
  });

  it('is false for every toggled-off/on legacy variant', () => {
    const variants: Partial<StudySettingsDto>[] = [
      { showCardStatus: true },
      { showFuriganaOnFront: true },
      { exampleSentencePosition: 'Front' },
      { exampleSentencePosition: 'Back', blurExampleSentence: true },
      {
        showConfusableReadings: true,
        showFrequencyRank: true,
        showPitchAccent: true,
        showKanjiBreakdown: true,
        showWordComposition: true,
        showWordUsedIn: true,
      },
    ];
    for (const v of variants) {
      expect(hasCustomArrangement(buildLayoutFromLegacySettings(settings(v)))).toBe(false);
    }
  });

  it('is true when a block type is duplicated', () => {
    expect(hasCustomArrangement(layout([b('headword')], [b('definitions'), b('definitions')]))).toBe(true);
  });

  it('is true when a divider is present', () => {
    expect(hasCustomArrangement(layout([b('headword'), b('divider')], [b('definitions')]))).toBe(true);
  });

  it('is true when a block sits on a non-canonical side', () => {
    expect(hasCustomArrangement(layout([b('headword'), b('pitchAccent')], [b('definitions')]))).toBe(true);
  });

  it('is true when the canonical types are reordered', () => {
    expect(hasCustomArrangement(layout([b('headword')], [b('definitions'), b('etymology')]))).toBe(true);
  });

  it('ignores frequencyRank order (the editor re-appends it at the end)', () => {
    expect(hasCustomArrangement(layout([b('headword')], [b('definitions'), b('deckOccurrences'), b('frequencyRank')]))).toBe(false);
  });
});
