// https://nuxt.com/docs/api/configuration/nuxt-config

import Aura from '@primeuix/themes/aura';
import { definePreset } from '@primeuix/styled';
import tailwindcss from '@tailwindcss/vite';
import path from 'node:path';
import * as fs from 'node:fs';

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

export default defineNuxtConfig({
  compatibilityDate: '2025-07-14',
  devtools: { enabled: true },
  features: {
    inlineStyles: false,
  },
  runtimeConfig: {
    public: {
      baseURL: 'https://localhost:7299/api/',
      googleSignInClientId: process.env.NUXT_PUBLIC_GOOGLE_SIGNIN_CLIENT_ID || '',
      ...(process.env.NUXT_PUBLIC_RECAPTCHA_V2_SITE_KEY
        ? {
            recaptcha: {
              v2SiteKey: process.env.NUXT_PUBLIC_RECAPTCHA_V2_SITE_KEY,
            },
          }
        : {}),
    },
  },
  modules: [
    '@nuxt/eslint',
    '@primevue/nuxt-module',
    '@nuxt/icon',
    '@pinia/nuxt',
    '@nuxtjs/seo',
    '@nuxt/scripts',
    ...(process.env.NUXT_PUBLIC_SCRIPTS_UMAMI_ANALYTICS_WEBSITE_ID ? ['nuxt-umami'] : []),
    ...(process.env.NUXT_PUBLIC_GOOGLE_SIGNIN_CLIENT_ID ? ['nuxt-vue3-google-signin'] : []),
    ...(process.env.NUXT_PUBLIC_RECAPTCHA_V2_SITE_KEY ? ['vue-recaptcha/nuxt'] : []),
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
  vite: {
    plugins: [tailwindcss()],
    build: {
      rollupOptions: {
        external: ['open'],
      },
    },
  },
  css: ['~/assets/css/main.css'],
  sitemap: {
    sources: ['/api/__sitemap__/urls'],
  },
  routeRules: {
    '/_nuxt/**': { ssr: false },
    '/.well-known/**': { ssr: false },
  },
  app: {
    head: {
      title: 'Jiten',
      htmlAttrs: {
        lang: 'en',
      },
      script: [
        {
          innerHTML: `(function(){try{var r=(document.cookie.match(/jiten-theme-mode=([^;]+)/)||[])[1];var m=r?decodeURIComponent(r).replace(/^"|"$/g,''):'auto';var d=m==='dark'||(m!=='light'&&window.matchMedia('(prefers-color-scheme:dark)').matches);if(d)document.documentElement.classList.add('dark-mode')}catch(e){}})()`,
          tagPosition: 'head',
        },
      ],
      link: [
        { rel: 'icon', type: 'image/svg+xml', href: '/favicon.svg' },
        { rel: 'icon', type: 'image/png', sizes: '96x96', href: '/favicon-96x96.png' },
        { rel: 'icon', type: 'image/x-icon', sizes: '48x48', href: '/favicon.ico' },
        { rel: 'apple-touch-icon', sizes: '180x180', href: '/apple-touch-icon.png' },
        { rel: 'manifest', href: '/site.webmanifest' },
        { rel: 'preconnect', href: 'https://cdn.jiten.moe' },
      ],
    },
  },
  site: {
    url: 'https://jiten.moe',
    name: 'Jiten',
    description: 'Vocabulary lists and Anki decks for all your Japanese media.',
  },
  schemaOrg: {
    identity: {
      type: 'Organization',
      name: 'Jiten',
      url: 'https://jiten.moe',
      logo: 'https://jiten.moe/web-app-manifest-512x512.png',
      sameAs: ['https://github.com/Sirush/Jiten'],
    },
  },
  ogImage: {
    fonts: [
      {
        name: 'Noto Sans JP',
        weight: 400,
        path: '/fonts/NotoSansJP-Regular.ttf',
      },
    ],
  },
  ...(process.env.NUXT_PUBLIC_SCRIPTS_UMAMI_ANALYTICS_WEBSITE_ID
    ? {
        umami: {
          id: process.env.NUXT_PUBLIC_SCRIPTS_UMAMI_ANALYTICS_WEBSITE_ID,
          host: process.env.NUXT_PUBLIC_SCRIPTS_UMAMI_ANALYTICS_HOST_URL || '',
          autoTrack: true,
          proxy: 'cloak',
          ignoreLocalhost: true,
        },
      }
    : {}),
  ...(process.env.NUXT_PUBLIC_GOOGLE_SIGNIN_CLIENT_ID
    ? {
        googleSignIn: {
          clientId: process.env.NUXT_PUBLIC_GOOGLE_SIGNIN_CLIENT_ID,
        },
      }
    : {}),
  devServer:
    process.env.NODE_ENV === 'development'
      ? {
          https: {
            key: fs.readFileSync(path.resolve(__dirname, 'localhost-key.pem')).toString(),
            cert: fs.readFileSync(path.resolve(__dirname, 'localhost.pem')).toString(),
          },
        }
      : {},
});
