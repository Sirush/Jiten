import type { DifficultyPalette } from '~/utils/displayProfile';

export const difficultyNames = ['Beginner', 'Easy', 'Average', 'Hard', 'Expert', 'Insane'] as const;

export const AVERAGE_BAND = difficultyNames.indexOf('Average');

export function difficultyBand(difficulty: number): number {
  return Math.min(Math.max(Math.floor(difficulty), 0), difficultyNames.length - 1);
}

interface PaletteColours {
  text: readonly string[];
  chart: readonly string[];
  harder: string;
  easier: string;
}

// The two alternatives keep easy and hard on hue axes each colour blindness still separates, and step lightness too.
export const DIFFICULTY_PALETTES: Record<DifficultyPalette, PaletteColours> = {
  default: {
    text: [
      'text-green-700 dark:text-green-300',
      'text-green-500 dark:text-green-200',
      'text-cyan-500 dark:text-cyan-300',
      'text-amber-600 dark:text-amber-300',
      'text-red-600 dark:text-red-300',
      'text-red-600 dark:text-red-300',
    ],
    chart: ['rgba(21, 128, 61, 0.8)', 'rgba(34, 197, 94, 0.8)', 'rgba(6, 182, 212, 0.8)', 'rgba(217, 119, 6, 0.8)', 'rgba(220, 38, 38, 0.8)', 'rgba(220, 38, 38, 0.8)'],
    harder: 'text-amber-500 dark:text-amber-400',
    easier: 'text-sky-500 dark:text-sky-400',
  },
  redGreen: {
    text: [
      'text-blue-700 dark:text-blue-300',
      'text-sky-600 dark:text-sky-300',
      'text-slate-500 dark:text-slate-300',
      'text-amber-600 dark:text-amber-300',
      'text-orange-700 dark:text-orange-400',
      'text-orange-700 dark:text-orange-400',
    ],
    chart: ['rgba(29, 78, 216, 0.8)', 'rgba(2, 132, 199, 0.8)', 'rgba(100, 116, 139, 0.8)', 'rgba(217, 119, 6, 0.8)', 'rgba(194, 65, 12, 0.8)', 'rgba(194, 65, 12, 0.8)'],
    harder: 'text-orange-600 dark:text-orange-400',
    easier: 'text-blue-600 dark:text-blue-400',
  },
  blueYellow: {
    text: [
      'text-teal-700 dark:text-teal-300',
      'text-cyan-600 dark:text-cyan-300',
      'text-slate-500 dark:text-slate-300',
      'text-pink-600 dark:text-pink-300',
      'text-red-700 dark:text-red-400',
      'text-red-700 dark:text-red-400',
    ],
    chart: ['rgba(15, 118, 110, 0.8)', 'rgba(8, 145, 178, 0.8)', 'rgba(100, 116, 139, 0.8)', 'rgba(219, 39, 119, 0.8)', 'rgba(185, 28, 28, 0.8)', 'rgba(185, 28, 28, 0.8)'],
    harder: 'text-pink-600 dark:text-pink-400',
    easier: 'text-teal-600 dark:text-teal-400',
  },
};

export const difficultyTextClasses = DIFFICULTY_PALETTES.default.text;
export const difficultyChartColours = DIFFICULTY_PALETTES.default.chart;

export const peakColour = '#d20ca3';
export const peakColourRgba = 'rgba(210, 12, 163, 1)';
export const averageColour = 'rgba(6, 182, 212, 1)';

export function getDifficultyTextClass(difficulty: number, palette: DifficultyPalette = 'default'): string {
  return DIFFICULTY_PALETTES[palette].text[difficultyBand(difficulty)]!;
}

export function getDifficultyChartColour(difficulty: number, palette: DifficultyPalette = 'default'): string {
  return DIFFICULTY_PALETTES[palette].chart[difficultyBand(difficulty)]!;
}

export function getDifficultyName(difficulty: number): string {
  return difficultyNames[difficultyBand(difficulty)]!;
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
