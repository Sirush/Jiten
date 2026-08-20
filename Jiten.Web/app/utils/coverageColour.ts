const STOPS: { at: number; rgb: [number, number, number] }[] = [
  { at: 50, rgb: [229, 57, 53] }, // red
  { at: 70, rgb: [255, 165, 0] }, // orange
  { at: 80, rgb: [254, 222, 0] }, // yellow
  { at: 90, rgb: [212, 225, 87] }, // yellow-green
  { at: 97, rgb: [76, 175, 80] }, // green
];

export function getCoverageColour(coverage: number, alpha: number = 1): string {
  const first = STOPS[0]!;
  const last = STOPS[STOPS.length - 1]!;
  let rgb = coverage <= first.at ? first.rgb : last.rgb;
  for (let i = 0; i < STOPS.length - 1; i++) {
    const a = STOPS[i]!;
    const b = STOPS[i + 1]!;
    if (coverage > a.at && coverage <= b.at) {
      const t = (coverage - a.at) / (b.at - a.at);
      rgb = [0, 1, 2].map((c) => Math.round(a.rgb[c]! + (b.rgb[c]! - a.rgb[c]!) * t)) as [number, number, number];
      break;
    }
  }
  return `rgba(${rgb[0]}, ${rgb[1]}, ${rgb[2]}, ${alpha})`;
}
