import { describe, expect, it } from 'vitest';
import { BUILT_IN_PRESETS, SHARE_CODE_PREFIX, decodeLayoutShareCode, encodeLayoutShareCode, instantiatePreset } from '../app/utils/cardLayoutPresets';
import { buildLayoutFromLegacySettings, newBlockId } from '../app/utils/cardLayout';
import { DEFAULT_CARD_DISPLAY_SETTINGS } from '../app/utils/defaultStudySettings';
import type { CardBlockType, CardLayout, CardLayoutBlock, StudySettingsDto } from '../app/types/types';

const types = (blocks: { type: CardBlockType }[]) => blocks.map((b) => b.type);
const allIds = (l: CardLayout) => [...l.front, ...l.back].map((b) => b.id);

function b(type: CardBlockType, options?: CardLayoutBlock['options']): CardLayoutBlock {
  return options ? { id: newBlockId(), type, options } : { id: newBlockId(), type };
}
function layout(front: CardLayoutBlock[], back: CardLayoutBlock[]): CardLayout {
  return { version: 1, front, back };
}

// Reproduces the util's url-safe base64 so tests can forge arbitrary payloads.
function makeCode(payload: unknown): string {
  const b64 = Buffer.from(JSON.stringify(payload), 'utf-8').toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return SHARE_CODE_PREFIX + b64;
}

describe('encode / decode round-trip', () => {
  it('preserves types, sides and order with fresh unique ids', () => {
    const original = layout(
      [b('headword', { furigana: 'hidden' }), b('exampleSentence', { blur: true })],
      [b('exampleSentence'), b('definitions'), b('pitchAccent')]
    );
    const decoded = decodeLayoutShareCode(encodeLayoutShareCode(original));
    expect(decoded).not.toBeNull();
    expect(types(decoded!.layout.front)).toEqual(['headword', 'exampleSentence']);
    expect(types(decoded!.layout.back)).toEqual(['exampleSentence', 'definitions', 'pitchAccent']);
    expect(decoded!.droppedTypes).toEqual([]);

    const newIds = allIds(decoded!.layout);
    expect(new Set(newIds).size).toBe(newIds.length);
    for (const id of newIds) expect(id).toBeTruthy();
    const shared = newIds.filter((id) => allIds(original).includes(id));
    expect(shared).toEqual([]);
  });

  it('preserves options including falsey booleans', () => {
    const original = layout([b('headword', { furigana: 'shown', showAudioButton: false })], []);
    const decoded = decodeLayoutShareCode(encodeLayoutShareCode(original));
    expect(decoded!.layout.front[0]!.options).toEqual({ furigana: 'shown', showAudioButton: false });
  });

  it('omits null-valued options on encode', () => {
    const original = layout([], [b('definitions', { maxDefinitions: null })]);
    const decoded = decodeLayoutShareCode(encodeLayoutShareCode(original));
    expect(decoded!.layout.back[0]!.options).toBeUndefined();
  });

  it('round-trips the card image block with its options', () => {
    const original = layout([b('headword'), b('cardImage', { layout: 'below', blur: false })], [b('definitions')]);
    const decoded = decodeLayoutShareCode(encodeLayoutShareCode(original));
    expect(types(decoded!.layout.front)).toEqual(['headword', 'cardImage']);
    expect(decoded!.layout.front[1]!.options).toEqual({ layout: 'below', blur: false });
  });

  it('round-trips the per-block size, spoiler, hideHeading, divider label and maxDefinitions options', () => {
    const original = layout(
      [b('divider', { style: 'space', label: 'Notes' })],
      [
        b('headword', { size: 'large' }),
        b('definitions', { size: 'small', spoiler: true, maxDefinitions: 3 }),
        b('pitchAccent', { hideHeading: true, spoiler: true }),
        b('customMeaning', { size: 'small', spoiler: true }),
      ]
    );
    const decoded = decodeLayoutShareCode(encodeLayoutShareCode(original));
    expect(decoded!.layout.front[0]!.options).toEqual({ style: 'space', label: 'Notes' });
    expect(decoded!.layout.back[0]!.options).toEqual({ size: 'large' });
    expect(decoded!.layout.back[1]!.options).toEqual({ size: 'small', spoiler: true, maxDefinitions: 3 });
    expect(decoded!.layout.back[2]!.options).toEqual({ hideHeading: true, spoiler: true });
    expect(decoded!.layout.back[3]!.options).toEqual({ size: 'small', spoiler: true });
  });
});

describe('malformed share codes', () => {
  it('returns null for a wrong prefix', () => {
    expect(decodeLayoutShareCode('nope.abcdef')).toBeNull();
    expect(decodeLayoutShareCode('jitenlayout2.abcdef')).toBeNull();
  });

  it('returns null for invalid base64', () => {
    expect(decodeLayoutShareCode(SHARE_CODE_PREFIX + '@@@not base64@@@')).toBeNull();
  });

  it('returns null for base64 that is not JSON', () => {
    const notJson = Buffer.from('this is not json', 'utf-8').toString('base64').replace(/=+$/, '');
    expect(decodeLayoutShareCode(SHARE_CODE_PREFIX + notJson)).toBeNull();
  });

  it('returns null for a wrong version', () => {
    expect(decodeLayoutShareCode(makeCode({ version: 2, front: [], back: [] }))).toBeNull();
  });

  it('returns null for a non-string / empty input', () => {
    expect(decodeLayoutShareCode('')).toBeNull();
    expect(decodeLayoutShareCode(SHARE_CODE_PREFIX)).toBeNull();
  });
});

describe('validation on decode', () => {
  it('drops unknown types and reports them', () => {
    const decoded = decodeLayoutShareCode(makeCode({ version: 1, front: [{ type: 'headword' }, { type: 'futureBlock' }], back: [{ type: 'anotherFuture' }] }));
    expect(types(decoded!.layout.front)).toEqual(['headword']);
    expect(types(decoded!.layout.back)).toEqual([]);
    expect(decoded!.droppedTypes.sort()).toEqual(['anotherFuture', 'futureBlock']);
  });

  it('clamps each side to 30 blocks', () => {
    const many = Array.from({ length: 40 }, () => ({ type: 'headword' }));
    const decoded = decodeLayoutShareCode(makeCode({ version: 1, front: many, back: many }));
    expect(decoded!.layout.front).toHaveLength(30);
    expect(decoded!.layout.back).toHaveLength(30);
  });

  it('strips unknown option keys while keeping known ones', () => {
    const decoded = decodeLayoutShareCode(
      makeCode({ version: 1, front: [{ type: 'headword', options: { furigana: 'shown', bogus: 'x', evil: 42 } }], back: [] })
    );
    expect(decoded!.layout.front[0]!.options).toEqual({ furigana: 'shown' });
  });

  it('ignores options on types that accept none', () => {
    const decoded = decodeLayoutShareCode(makeCode({ version: 1, front: [{ type: 'cardStatus', options: { foo: 1 } }], back: [] }));
    expect(decoded!.layout.front[0]!.options).toBeUndefined();
  });

  it('keeps whitelisted new option keys while stripping unknown ones on option-bearing types', () => {
    const decoded = decodeLayoutShareCode(
      makeCode({ version: 1, front: [], back: [{ type: 'pitchAccent', options: { hideHeading: true, spoiler: false, bogus: 'x' } }] })
    );
    expect(decoded!.layout.back[0]!.options).toEqual({ hideHeading: true, spoiler: false });
  });

  it('accepts the card image layout and blur option keys', () => {
    const decoded = decodeLayoutShareCode(
      makeCode({ version: 1, front: [{ type: 'cardImage', options: { layout: 'below', blur: false, bogus: 'x' } }], back: [] })
    );
    expect(decoded!.layout.front[0]!.options).toEqual({ layout: 'below', blur: false });
  });

  it('tolerates a missing side', () => {
    const decoded = decodeLayoutShareCode(makeCode({ version: 1, front: [{ type: 'headword' }] }));
    expect(types(decoded!.layout.front)).toEqual(['headword']);
    expect(decoded!.layout.back).toEqual([]);
  });
});

describe('built-in presets', () => {
  it('all encode, decode and validate cleanly', () => {
    for (const preset of BUILT_IN_PRESETS) {
      const decoded = decodeLayoutShareCode(encodeLayoutShareCode(preset.layout));
      expect(decoded, preset.name).not.toBeNull();
      expect(decoded!.droppedTypes, preset.name).toEqual([]);
      expect(types(decoded!.layout.front), preset.name).toEqual(types(preset.layout.front));
      expect(types(decoded!.layout.back), preset.name).toEqual(types(preset.layout.back));
    }
  });

  it('Default matches the builder output on default settings', () => {
    const preset = BUILT_IN_PRESETS.find((p) => p.name === 'Default')!;
    const built = buildLayoutFromLegacySettings(DEFAULT_CARD_DISPLAY_SETTINGS as StudySettingsDto);
    expect(types(preset.layout.front)).toEqual(types(built.front));
    expect(types(preset.layout.back)).toEqual(types(built.back));
    const optsOf = (l: CardLayout) => [...l.front, ...l.back].map((x) => x.options ?? null);
    expect(optsOf(preset.layout)).toEqual(optsOf(built));
  });

  it('has the four documented presets', () => {
    expect(BUILT_IN_PRESETS.map((p) => p.name)).toEqual(['Default', 'Minimal', 'Sentence-first', 'Listening']);
  });

  it('Default preset leads with the card status block, matching the server default', () => {
    const preset = BUILT_IN_PRESETS.find((p) => p.name === 'Default')!;
    expect(preset.layout.front[0]!.type).toBe('cardStatus');
  });
});

describe('instantiatePreset', () => {
  it('clones a layout with fresh ids and independent options', () => {
    const src = BUILT_IN_PRESETS.find((p) => p.name === 'Sentence-first')!.layout;
    const copy = instantiatePreset(src);
    expect(types(copy.front)).toEqual(types(src.front));
    const shared = allIds(copy).filter((id) => allIds(src).includes(id));
    expect(shared).toEqual([]);
    copy.front[0]!.options!.blur = false;
    expect(src.front[0]!.options!.blur).toBe(true);
  });
});
