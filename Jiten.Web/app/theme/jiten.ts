import Aura from '@primeuix/themes/aura';
import { definePreset } from '@primeuix/styled';

// Loaded via primevue.importTheme (build-time import) so the preset is not serialised
// into __NUXT__.config on every SSR response.
const JitenPreset = definePreset(Aura, {
  // Halved Aura scale; must stay in sync with the --radius-* overrides in main.css so that
  // hand-built Tailwind elements match PrimeVue components sitting next to them.
  primitive: {
    borderRadius: {
      none: '0',
      xs: '1px',
      sm: '2px',
      md: '3px',
      lg: '4px',
      xl: '6px',
    },
  },
  semantic: {
    primary: {
      50: '{purple.50}',
      100: '{purple.100}',
      200: '{purple.200}',
      300: '{purple.300}',
      400: '{purple.400}',
      500: '{purple.500}',
      600: '{purple.600}',
      700: '{purple.700}',
      800: '{purple.800}',
      900: '{purple.900}',
      950: '{purple.950}',
    },
    colorScheme: {
      dark: {
        surface: {
          0: '#ffffff',
          50: '{neutral.50}',
          100: '{neutral.100}',
          200: '{neutral.200}',
          300: '{neutral.300}',
          400: '{neutral.400}',
          500: '{neutral.500}',
          600: '{neutral.600}',
          700: '{neutral.700}',
          800: '{neutral.800}',
          900: '{neutral.900}',
          950: '{neutral.950}',
        },
      },
    },
  },
  components: {
    card: {
      caption: {
        gap: '0',
      },
      body: {
        padding: '1rem',
      },
    },
  },
});

export default {
  preset: JitenPreset,
  options: {
    darkModeSelector: '.dark-mode',
  },
};
