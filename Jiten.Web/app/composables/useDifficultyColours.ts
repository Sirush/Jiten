import { useJitenStore } from '~/stores/jitenStore';
import { DIFFICULTY_PALETTES, difficultyBand } from '~/utils/difficultyColours';

export function useDifficultyColours() {
  const store = useJitenStore();
  const palette = computed(() => DIFFICULTY_PALETTES[store.difficultyPalette] ?? DIFFICULTY_PALETTES.default);

  return {
    paletteId: computed(() => store.difficultyPalette),
    textClass: (difficulty: number) => palette.value.text[difficultyBand(difficulty)]!,
    adjustmentClass: (adjustment: number) => (adjustment > 0 ? palette.value.harder : palette.value.easier),
    chartColour: (difficulty: number, alpha?: number) => {
      const colour = palette.value.chart[difficultyBand(difficulty)]!;
      return alpha === undefined ? colour : colour.replace(/[\d.]+\)$/, `${alpha})`);
    },
  };
}
