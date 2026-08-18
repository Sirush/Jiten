export function getCoverageColour(coverage: number): string {
  if (coverage < 50) return 'red';
  if (coverage < 70) return '#FFA500';
  if (coverage < 80) return '#FEDE00';
  if (coverage < 90) return '#D4E157';
  return '#4CAF50';
}

export function getCoverageBorder(coverage: number, borderWidth: string = '2px'): string {
  return `${borderWidth} solid ${getCoverageColour(coverage)}`;
}
