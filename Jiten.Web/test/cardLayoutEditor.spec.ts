import { describe, expect, it } from 'vitest';
import { buildLayoutFromLegacySettings, moveBlock, resolveCardLayout } from '../app/utils/cardLayout';
import type { CardBlockType, CardLayout, CardLayoutBlock, StudySettingsDto } from '../app/types/types';

function block(id: string, type: CardBlockType = 'divider'): CardLayoutBlock {
  return { id, type };
}

const types = (blocks: CardLayoutBlock[]) => blocks.map((b) => b.id);

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

describe('moveBlock', () => {
  const front = () => [block('a'), block('b'), block('c')];
  const back = () => [block('x'), block('y')];

  it('moves a block down within the front list', () => {
    // Drop index is computed against the list with the dragged element removed (as the composable does).
    const r = moveBlock(front(), back(), { list: 'front', index: 0 }, { list: 'front', index: 1 });
    expect(types(r.front)).toEqual(['b', 'a', 'c']);
    expect(types(r.back)).toEqual(['x', 'y']);
  });

  it('moves a block up within the front list', () => {
    const r = moveBlock(front(), back(), { list: 'front', index: 2 }, { list: 'front', index: 0 });
    expect(types(r.front)).toEqual(['c', 'a', 'b']);
  });

  it('is a no-op-equivalent when dropped back at its own index', () => {
    const r = moveBlock(front(), back(), { list: 'front', index: 1 }, { list: 'front', index: 1 });
    expect(types(r.front)).toEqual(['a', 'b', 'c']);
  });

  it('moves a block across lists at a specific index', () => {
    const r = moveBlock(front(), back(), { list: 'front', index: 0 }, { list: 'back', index: 1 });
    expect(types(r.front)).toEqual(['b', 'c']);
    expect(types(r.back)).toEqual(['x', 'a', 'y']);
  });

  it('appends across lists and clamps an out-of-range target index', () => {
    const r = moveBlock(front(), back(), { list: 'back', index: 0 }, { list: 'front', index: 99 });
    expect(types(r.back)).toEqual(['y']);
    expect(types(r.front)).toEqual(['a', 'b', 'c', 'x']);
  });

  it('inserts at the head when dropping at index 0 of the other list', () => {
    const r = moveBlock(front(), back(), { list: 'back', index: 1 }, { list: 'front', index: 0 });
    expect(types(r.front)).toEqual(['y', 'a', 'b', 'c']);
    expect(types(r.back)).toEqual(['x']);
  });

  it('returns the inputs unchanged when the source index is out of range', () => {
    const f = front();
    const b = back();
    const r = moveBlock(f, b, { list: 'front', index: 5 }, { list: 'back', index: 0 });
    expect(types(r.front)).toEqual(['a', 'b', 'c']);
    expect(types(r.back)).toEqual(['x', 'y']);
  });

  it('does not mutate the input arrays', () => {
    const f = front();
    const b = back();
    moveBlock(f, b, { list: 'front', index: 0 }, { list: 'back', index: 0 });
    expect(types(f)).toEqual(['a', 'b', 'c']);
    expect(types(b)).toEqual(['x', 'y']);
  });
});

describe('resolveCardLayout precedence', () => {
  it('derives from the legacy toggles when no explicit layout is set', () => {
    const s = settings({ showPitchAccent: true });
    const resolved = resolveCardLayout(s);
    const derived = buildLayoutFromLegacySettings(s);
    expect(resolved.back.map((b) => b.type)).toEqual(derived.back.map((b) => b.type));
    expect(resolved.back.some((b) => b.type === 'pitchAccent')).toBe(true);
  });

  it('returns the explicit layout verbatim and ignores the toggles', () => {
    const explicit: CardLayout = {
      version: 1,
      front: [block('h', 'headword')],
      back: [block('d', 'definitions')],
    };
    // Toggles that would otherwise add many blocks are present but must be ignored.
    const s = settings({ cardLayout: explicit, showPitchAccent: true, showKanjiBreakdown: true, showCardStatus: true });
    const resolved = resolveCardLayout(s);
    expect(resolved).toBe(explicit);
    expect(resolved.front.map((b) => b.type)).toEqual(['headword']);
    expect(resolved.back.map((b) => b.type)).toEqual(['definitions']);
  });

  it('honours an explicit empty layout instead of re-deriving from toggles (materialised clear)', () => {
    const empty: CardLayout = { version: 1, front: [], back: [] };
    const resolved = resolveCardLayout(settings({ cardLayout: empty, showPitchAccent: true }));
    expect(resolved.front).toHaveLength(0);
    expect(resolved.back).toHaveLength(0);
  });
});

describe('materialisation of a legacy layout', () => {
  it('produces an explicit layout that no longer tracks the toggles once edited', () => {
    const s = settings({ showConfusableReadings: true });
    // First edit materialises the derived layout into a concrete object.
    const materialised = buildLayoutFromLegacySettings(s);
    const edited = moveBlock(materialised.front, materialised.back, { list: 'front', index: 0 }, { list: 'back', index: 0 });
    const layout: CardLayout = { version: 1, front: edited.front, back: edited.back };

    // Changing a toggle afterwards must not affect the materialised layout when it is the source of truth.
    const after = resolveCardLayout(settings({ cardLayout: layout, showConfusableReadings: false, showPitchAccent: true }));
    expect(after).toBe(layout);
    expect(after.front.some((b) => b.type === 'confusableReadings')).toBe(true);
    expect(after.back.some((b) => b.type === 'pitchAccent')).toBe(false);
  });
});
