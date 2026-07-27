export const difficultyNames = ['Beginner', 'Easy', 'Average', 'Hard', 'Expert', 'Insane'] as const;

export const difficultyTextClasses = [
  'text-green-700 dark:text-green-300', // Beginner
  'text-green-500 dark:text-green-200', // Easy
  'text-cyan-500 dark:text-cyan-300', // Average
  'text-amber-600 dark:text-amber-300', // Hard
  'text-red-600 dark:text-red-300', // Expert
  'text-red-600 dark:text-red-300', // Insane
] as const;

export const difficultyChartColours = [
  'rgba(21, 128, 61, 0.8)', // green-700 - Beginner
  'rgba(34, 197, 94, 0.8)', // green-500 - Easy
  'rgba(6, 182, 212, 0.8)', // cyan-500 - Average
  'rgba(217, 119, 6, 0.8)', // amber-600 - Hard
  'rgba(220, 38, 38, 0.8)', // red-600 - Expert
  'rgba(220, 38, 38, 0.8)', // red-600 - Insane
] as const;

export const peakColour = '#d20ca3';
export const peakColourRgba = 'rgba(210, 12, 163, 1)';
export const averageColour = 'rgba(6, 182, 212, 1)';

export function getDifficultyTextClass(difficulty: number): string {
  const index = Math.min(Math.max(Math.floor(difficulty), 0), difficultyTextClasses.length - 1);
  return difficultyTextClasses[index];
}

export function getDifficultyChartColour(difficulty: number): string {
  const index = Math.min(Math.max(Math.floor(difficulty), 0), difficultyChartColours.length - 1);
  return difficultyChartColours[index];
}

export function getDifficultyName(difficulty: number): string {
  const index = Math.min(Math.max(Math.floor(difficulty), 0), difficultyNames.length - 1);
  return difficultyNames[index];
}

export function formatDifficultyValue(difficulty: number, usePercentage: boolean, decimals: number = 2): string {
  const clamped = Math.min(Math.max(difficulty, 0), 5);
  if (usePercentage) {
    return `${(clamped * 20).toFixed(0)}%`;
  }
  return `${clamped.toFixed(decimals)}/5`;
}

export function getMaxDifficultyLabel(usePercentage: boolean): string {
  return usePercentage ? '100%' : '5';
}
