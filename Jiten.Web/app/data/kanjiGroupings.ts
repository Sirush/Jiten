import type { KanjiGridItem } from '~/types';
import { kankenGroups } from './kanken';
import { wanikaniGroups } from './wanikani';
import { rtkGroups } from './rtk';
import { klcGroups } from './klc';
import { tmwGroups } from './tmw';
import { jlptGroups } from './jlpt';

export interface KanjiGroup {
  name: string;
  kanji: KanjiGridItem[];
}

// KANJIDIC's <jlpt> tag is the obsolete 4-level scale (no N5). Derive the modern
// N5–N1 level from the Tanos-based jlptGroups lists instead of the DB field.
const jlptCharToLevel = (() => {
  const map = new Map<string, number>();
  for (const g of jlptGroups) {
    const level = Number(g.name.slice(1));
    for (const ch of g.characters) {
      if (!map.has(ch)) map.set(ch, level);
    }
  }
  return map;
})();

export function jlptLevelForKanji(character: string): number | null {
  return jlptCharToLevel.get(character) ?? null;
}

export function jlptLabelForKanji(character: string): string | null {
  const level = jlptCharToLevel.get(character);
  return level ? `N${level}` : null;
}

export type KanjiScale = 'jlpt' | 'grade' | 'kanken' | 'wanikani' | 'rtk' | 'klc' | 'tmw';
export type KanjiScalePref = KanjiScale | 'none';

export const kanjiScaleOptions: { label: string; value: KanjiScalePref }[] = [
  { label: 'JLPT', value: 'jlpt' },
  { label: 'Jouyou Grade', value: 'grade' },
  { label: 'Kanken', value: 'kanken' },
  { label: 'WaniKani', value: 'wanikani' },
  { label: 'RTK', value: 'rtk' },
  { label: 'KLC', value: 'klc' },
  { label: 'TMW', value: 'tmw' },
  { label: 'None', value: 'none' },
];

const scaleGroups: Record<Exclude<KanjiScale, 'grade'>, { name: string; characters: string }[]> = {
  jlpt: jlptGroups,
  kanken: kankenGroups,
  wanikani: wanikaniGroups,
  rtk: rtkGroups,
  klc: klcGroups,
  tmw: tmwGroups,
};

const scalePrefix: Record<KanjiScale, string> = {
  jlpt: 'JLPT', grade: 'Grade', kanken: 'Kanken', wanikani: 'WaniKani', rtk: 'RTK', klc: 'KLC', tmw: 'TMW',
};

const scaleCharMaps = new Map<Exclude<KanjiScale, 'grade'>, Map<string, string>>();

function charToGroupName(scale: Exclude<KanjiScale, 'grade'>, character: string): string | null {
  let map = scaleCharMaps.get(scale);
  if (!map) {
    map = new Map<string, string>();
    for (const g of scaleGroups[scale]) {
      for (const ch of g.characters) {
        if (!map.has(ch)) map.set(ch, g.name);
      }
    }
    scaleCharMaps.set(scale, map);
  }
  return map.get(character) ?? null;
}

// Short badge label for a kanji on a given scale (e.g. "JLPT N5", "Kanken Level 10",
// "WaniKani Level 2", "KLC Volume 1", "TMW Student", "Grade 1"). Returns null if the
// kanji is not part of that list.
export function kanjiScaleMembership(character: string, scale: KanjiScale, grade?: number | null): string | null {
  if (scale === 'grade') {
    if (!grade) return null;
    return gradeNames[grade] ?? `Grade ${grade}`;
  }
  const name = charToGroupName(scale, character);
  if (name == null) return null;
  if (scale === 'rtk') return scalePrefix.rtk; // single list — group name isn't informative
  return `${scalePrefix[scale]} ${name.replace(/\s*Kanji$/, '')}`;
}

export type DisplayType = 'none' | 'jlpt' | 'grade' | 'frequency' | 'strokeCount' | 'kanken' | 'wanikani' | 'rtk' | 'klc' | 'tmw';

export const displayTypeOptions: { label: string; value: DisplayType }[] = [
  { label: 'Frequency (flat)', value: 'none' },
  { label: 'JLPT Level', value: 'jlpt' },
  { label: 'Jouyou Grade', value: 'grade' },
  { label: 'Frequency Range', value: 'frequency' },
  { label: 'Stroke Count', value: 'strokeCount' },
  { label: 'Kanken Level', value: 'kanken' },
  { label: 'WaniKani Level', value: 'wanikani' },
  { label: 'RTK', value: 'rtk' },
  { label: 'KLC', value: 'klc' },
  { label: 'TMW (TheMoeWay)', value: 'tmw' },
];

const gradeNames: Record<number, string> = {
  1: 'Grade 1', 2: 'Grade 2', 3: 'Grade 3', 4: 'Grade 4',
  5: 'Grade 5', 6: 'Grade 6', 8: 'Secondary', 9: 'Jinmeiyou', 10: 'Jinmeiyou (variant)',
};

function groupByExternalData(
  kanji: KanjiGridItem[],
  groups: { name: string; characters: string }[],
  leftoverName: string,
): KanjiGroup[] {
  const charToGroup = new Map<string, number>();
  for (let i = 0; i < groups.length; i++) {
    for (const ch of groups[i].characters) {
      if (!charToGroup.has(ch)) charToGroup.set(ch, i);
    }
  }

  const buckets: KanjiGridItem[][] = groups.map(() => []);
  const leftover: KanjiGridItem[] = [];

  for (const k of kanji) {
    const idx = charToGroup.get(k.character);
    if (idx !== undefined) buckets[idx].push(k);
    else leftover.push(k);
  }

  const result: KanjiGroup[] = [];
  for (let i = 0; i < groups.length; i++) {
    if (buckets[i].length > 0) {
      result.push({ name: groups[i].name, kanji: buckets[i] });
    }
  }
  if (leftover.length > 0) {
    result.push({ name: leftoverName, kanji: leftover });
  }
  return result;
}

function groupByProperty(
  kanji: KanjiGridItem[],
  keyFn: (k: KanjiGridItem) => string | number | null,
  nameMap: Record<string | number, string>,
  sortKeys: (string | number)[],
  leftoverName: string,
): KanjiGroup[] {
  const buckets = new Map<string | number | null, KanjiGridItem[]>();
  for (const k of kanji) {
    const key = keyFn(k);
    if (!buckets.has(key)) buckets.set(key, []);
    buckets.get(key)!.push(k);
  }

  const result: KanjiGroup[] = [];
  for (const key of sortKeys) {
    const items = buckets.get(key);
    if (items?.length) {
      result.push({ name: nameMap[key] ?? String(key), kanji: items });
    }
  }
  const leftover = buckets.get(null);
  if (leftover?.length) {
    result.push({ name: leftoverName, kanji: leftover });
  }
  return result;
}

export function groupKanji(kanji: KanjiGridItem[], displayType: DisplayType): KanjiGroup[] {
  if (displayType === 'none') {
    return [{ name: '', kanji }];
  }

  if (displayType === 'jlpt') {
    return groupByExternalData(kanji, jlptGroups, 'Non-JLPT');
  }

  if (displayType === 'grade') {
    const keys = [1, 2, 3, 4, 5, 6, 8, 9, 10];
    return groupByProperty(kanji, k => k.grade, gradeNames, keys, 'Ungraded');
  }

  if (displayType === 'frequency') {
    const ranges = [500, 1000, 1500, 2000, 2500];
    const groups: KanjiGroup[] = ranges.map(() => ({ name: '', kanji: [] }));
    const overflow: KanjiGridItem[] = [];
    const unranked: KanjiGridItem[] = [];

    for (const k of kanji) {
      if (k.frequencyRank == null) { unranked.push(k); continue; }
      let placed = false;
      for (let i = 0; i < ranges.length; i++) {
        const lo = i === 0 ? 1 : ranges[i - 1] + 1;
        if (k.frequencyRank >= lo && k.frequencyRank <= ranges[i]) {
          groups[i].kanji.push(k);
          placed = true;
          break;
        }
      }
      if (!placed) overflow.push(k);
    }

    const result: KanjiGroup[] = [];
    for (let i = 0; i < ranges.length; i++) {
      const lo = i === 0 ? 1 : ranges[i - 1] + 1;
      if (groups[i].kanji.length > 0) {
        result.push({ name: `Rank ${lo}–${ranges[i]}`, kanji: groups[i].kanji });
      }
    }
    if (overflow.length > 0) result.push({ name: `Rank ${ranges[ranges.length - 1] + 1}+`, kanji: overflow });
    if (unranked.length > 0) result.push({ name: 'Unranked', kanji: unranked });
    return result;
  }

  if (displayType === 'strokeCount') {
    const map = new Map<number, KanjiGridItem[]>();
    for (const k of kanji) {
      const sc = k.strokeCount || 0;
      if (!map.has(sc)) map.set(sc, []);
      map.get(sc)!.push(k);
    }
    const sorted = [...map.entries()].sort((a, b) => a[0] - b[0]);
    return sorted.map(([sc, items]) => ({
      name: sc === 0 ? 'Unknown' : `${sc} strokes`,
      kanji: items,
    }));
  }

  if (displayType === 'kanken') return groupByExternalData(kanji, kankenGroups, 'Non-Kanken');
  if (displayType === 'wanikani') return groupByExternalData(kanji, wanikaniGroups, 'Not in WaniKani');
  if (displayType === 'rtk') return groupByExternalData(kanji, rtkGroups, 'Non-RTK');
  if (displayType === 'klc') return groupByExternalData(kanji, klcGroups, 'Non-KLC');
  if (displayType === 'tmw') return groupByExternalData(kanji, tmwGroups, 'Not in TMW');

  return [{ name: '', kanji }];
}
