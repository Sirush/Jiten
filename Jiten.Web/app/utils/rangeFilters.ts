const clamp = (val: number, min: number, max: number) => Math.min(max, Math.max(min, val));

export type RangeBounds = { min: number | null; max: number | null };

export const clampRange = (min: number | null, max: number | null, floor: number, ceil: number): RangeBounds => {
  const nextMin = min != null ? clamp(min, floor, ceil) : null;
  let nextMax = max != null ? clamp(max, floor, ceil) : null;

  if (nextMin != null && nextMax != null && nextMin > nextMax) nextMax = nextMin;

  return { min: nextMin, max: nextMax };
};

export const rangeChipLabel = (
  label: string,
  min: number | null,
  max: number | null,
  format: (value: number) => string = (value) => value.toLocaleString()
): string | null => {
  if (min == null && max == null) return null;
  if (min != null && max != null) return min === max ? `${label} ${format(min)}` : `${label} ${format(min)}-${format(max)}`;
  if (min != null) return `${label} ≥ ${format(min)}`;
  return `${label} ≤ ${format(max as number)}`;
};
