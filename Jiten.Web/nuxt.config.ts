// https://nuxt.com/docs/api/configuration/nuxt-config

import tailwindcss from '@tailwindcss/vite';
import path from 'node:path';
import * as fs from 'node:fs';

export default defineNuxtConfig({
  compatibilityDate: '2025-07-14',
  devtools: { enabled: true },
  features: {
    inlineStyles: false,
  },
  runtimeConfig: {
    // Server-only: shared secret sent on SSR-originated API calls so the API can exempt
    // first-party server rendering from the per-IP anonymous rate limit. Empty in dev.
    ssrBypassKey: process.env.NUXT_SSR_BYPASS_KEY || '',
    public: {
      baseURL: process.env.NUXT_PUBLIC_BASE_URL || 'https://localhost:7299/api/',
      googleSignInClientId: process.env.NUXT_PUBLIC_GOOGLE_SIGNIN_CLIENT_ID || '',
      legal: {
        publisherName: process.env.NUXT_PUBLIC_LEGAL_PUBLISHER_NAME || '',
        publicationDirector: process.env.NUXT_PUBLIC_LEGAL_PUBLICATION_DIRECTOR || '',
        siren: process.env.NUXT_PUBLIC_LEGAL_SIREN || '',
        siret: process.env.NUXT_PUBLIC_LEGAL_SIRET || '',
        address: process.env.NUXT_PUBLIC_LEGAL_ADDRESS || '',
      },
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
    '@nuxt/fonts',
    '@nuxt/content',
    '@nuxt/scripts',
    // Always registered so umTrackEvent exists at build time; without an id the module runs in faux mode and sends nothing.
    'nuxt-umami',
    ...(process.env.NUXT_PUBLIC_GOOGLE_SIGNIN_CLIENT_ID ? ['nuxt-vue3-google-signin'] : []),
    ...(process.env.NUXT_PUBLIC_RECAPTCHA_V2_SITE_KEY ? ['vue-recaptcha/nuxt'] : []),
  ],
  content: {
    // Use Node 22.5+ built-in node:sqlite — no native better-sqlite3 build needed in dev,
    // CI, or the Alpine production image (which runs Node 23).
    experimental: { sqliteConnector: 'native' },
  },
  experimental: {
    // Each deploy ships a fresh image, so the previous build's hashed /_nuxt chunks 404 instantly.
    // The default 'automatic' only recovers on router navigation, leaving hydration-time preload
    // failures to render the error page
    emitRouteChunkError: 'automatic-immediate',
  },
  primevue: {
    // Build-time theme import; putting the preset in `options.theme` instead would serialise
    // ~134KB of design tokens into __NUXT__.config on every SSR response.
    importTheme: { from: '~/theme/jiten.ts' },
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
    // Jiten+ member tools: no search value, and thin/paywalled for crawlers.
    exclude: ['/jiten-plus/frequency-lists', '/jiten-plus/immersion-plan'],
  },
  nitro: {
    // SSR is CPU-bound, so a single process caps throughput at one core; NITRO_CLUSTER_WORKERS sets the count at runtime.
    preset: 'node-cluster',
  },
  routeRules: {
    '/_nuxt/**': { ssr: false },
    '/.well-known/**': { ssr: false },
    // FAQ migrated into the Guides system; preserve existing ranking/backlinks.
    '/faq': { redirect: { to: '/guides', statusCode: 301 } },
    // Frequency lists moved, old URL kept for Yomitan and other backlinks
    '/other': { redirect: { to: '/frequency-dictionaries', statusCode: 301 } },
    // Legacy numeric media-type list URLs; slugs are the canonical form. Keep in sync with mediaTypeSlugMap.
    '/decks/media/list/1': { redirect: { to: '/decks/media/list/anime', statusCode: 301 } },
    '/decks/media/list/2': { redirect: { to: '/decks/media/list/drama', statusCode: 301 } },
    '/decks/media/list/3': { redirect: { to: '/decks/media/list/movies', statusCode: 301 } },
    '/decks/media/list/4': { redirect: { to: '/decks/media/list/novels', statusCode: 301 } },
    '/decks/media/list/5': { redirect: { to: '/decks/media/list/non-fiction', statusCode: 301 } },
    '/decks/media/list/6': { redirect: { to: '/decks/media/list/video-games', statusCode: 301 } },
    '/decks/media/list/7': { redirect: { to: '/decks/media/list/visual-novels', statusCode: 301 } },
    '/decks/media/list/8': { redirect: { to: '/decks/media/list/web-novels', statusCode: 301 } },
    '/decks/media/list/9': { redirect: { to: '/decks/media/list/manga', statusCode: 301 } },
    '/decks/media/list/10': { redirect: { to: '/decks/media/list/audio', statusCode: 301 } },
    '/mentions-legales': { robots: 'noindex, follow' },
    '/cgv': { robots: 'noindex, follow' },
    '/cgv-fr': { robots: 'noindex, follow' },
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
        // Build assets carry `crossorigin`, so the warmed connection must be anonymous-CORS
        ...(process.env.NUXT_APP_CDN_URL
          ? [{ rel: 'preconnect', href: process.env.NUXT_APP_CDN_URL, crossorigin: 'anonymous' as const }]
          : []),
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
      sameAs: [
        'https://github.com/Sirush/Jiten',
        'https://discord.gg/cZWM7b4wzk',
        'https://patreon.com/JitenMoe',
        'https://ko-fi.com/jiten',
      ],
    },
  },
  ogImage: {
    runtimeCacheStorage: {
      driver: 'lruCache',
      // Allocated per cluster worker, so the real footprint is this times NITRO_CLUSTER_WORKERS.
      max: 150,
      maxSize: 32 * 1024 * 1024,
    },
    // OG components render Japanese deck titles; without the japanese subset Satori falls back to tofu.
    fontSubsets: ['latin', 'japanese'],
  },
  fonts: {
    // Present only for nuxt-og-image's Satori renderer, which requires @nuxt/fonts for any
    // non-Inter font. Site text keeps loading through @fontsource-variable/noto-sans-jp in
    // main.css; remote providers are disabled so the module cannot inject fonts of its own.
    providers: {
      google: false,
      googleicons: false,
      bunny: false,
      fontshare: false,
      fontsource: false,
      adobe: false,
    },
    families: [
      {
        name: 'Noto Sans JP',
        src: '/fonts/NotoSansJP-Regular.ttf',
        weight: 400,
        global: true,
      },
    ],
  },
  umami: {
    id: process.env.NUXT_PUBLIC_SCRIPTS_UMAMI_ANALYTICS_WEBSITE_ID || '',
    host: process.env.NUXT_PUBLIC_SCRIPTS_UMAMI_ANALYTICS_HOST_URL || '',
    autoTrack: true,
    proxy: 'cloak',
    ignoreLocalhost: true,
  },
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
