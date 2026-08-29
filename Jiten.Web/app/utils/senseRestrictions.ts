import type { Definition, Reading } from '~/types';

export function isSenseRestricted(definition: Definition, currentReadingIndex: number | undefined, readings: Reading[] | undefined): boolean {
  const restrictions = definition.restrictedToReadingIndices;
  if (currentReadingIndex == null || !restrictions || restrictions.length === 0) return false;

  const typeByIndex = new Map<number, number>();
  for (const r of readings ?? []) typeByIndex.set(r.readingIndex, r.readingType);

  const currentType = typeByIndex.get(currentReadingIndex);
  if (currentType === undefined) return !restrictions.includes(currentReadingIndex);

  let sawSameAxis = false;
  for (const index of restrictions) {
    if (typeByIndex.get(index) !== currentType) continue;
    if (index === currentReadingIndex) return false;
    sawSameAxis = true;
  }
  return sawSameAxis;
}
