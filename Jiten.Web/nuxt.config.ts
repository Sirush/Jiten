// https://nuxt.com/docs/api/configuration/nuxt-config

import Aura from '@primeuix/themes/aura';
import { definePreset } from '@primeuix/styled';
import tailwindcss from '@tailwindcss/vite';

// Custom theming
const JitenPreset = definePreset(Aura, {
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

export default defineNuxtConfig({
  compatibilityDate: '2024-04-03',
  devtools: { enabled: true },
  runtimeConfig: {
    public: {
      baseURL: 'https://localhost:7299/api/',
      scripts: {
        umamiAnalytics: {
          websiteId: '',
          hostUrl: 'https://umami.jiten.moe',
        },
      },
    },
  },
  modules: [
    '@primevue/nuxt-module',
    '@nuxt/icon',
    '@nuxtjs/google-fonts',
    '@nuxt/eslint',
    '@pinia/nuxt',
    '@nuxtjs/seo',
    'nuxt-link-checker',
    '@nuxt/scripts',
  ],
  primevue: {
    options: {
      theme: {
        preset: JitenPreset,
        options: {
          darkModeSelector: '.dark-mode',
        },
      },
    },
  },
  googleFonts: {
    families: {
      'Noto+Sans+JP': [400, 700],
    },
    display: 'swap',
  },
  vite: {
    plugins: [tailwindcss()],
  },
  css: ['~/assets/css/main.css'],
  sitemap: {
    sources: ['/api/__sitemap__/urls'],
  },
  site: {
    url: 'https://jiten.moe',
    name: 'Vocabulary lists and anki decks for all your Japanese media',
  },
  scripts: {
    registry: {
      umamiAnalytics: true,
    },
  },
});
