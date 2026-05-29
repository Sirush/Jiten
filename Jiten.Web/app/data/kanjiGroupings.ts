import type { KanjiGridItem } from '~/types';
import { kankenGroups } from './kanken';
import { wanikaniGroups } from './wanikani';
import { rtkGroups } from './rtk';
import { klcGroups } from './klc';
import { tmwGroups } from './tmw';

export interface KanjiGroup {
  name: string;
  kanji: KanjiGridItem[];
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

const jlptNames: Record<number, string> = { 5: 'N5', 4: 'N4', 3: 'N3', 2: 'N2', 1: 'N1' };
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
    return groupByProperty(kanji, k => k.jlptLevel, jlptNames, [5, 4, 3, 2, 1], 'Non-JLPT');
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
