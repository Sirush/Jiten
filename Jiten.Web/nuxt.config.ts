// https://nuxt.com/docs/api/configuration/nuxt-config

import Lara from '@primevue/themes/lara';
import {definePreset} from "@primeuix/styled";

// Custom theming
const JitenPreset = definePreset(Lara, {
    semantic:{
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
            950: '{purple.950}'
        },
    },
    components: {
        card: {
            caption:{
                gap: "0"
            },
            body: {
                padding: "1rem"
            }
        }
    }
});

export default defineNuxtConfig({
    compatibilityDate: '2024-04-03',
    devtools: {enabled: true},
    runtimeConfig: {
        public: {
            baseURL: process.env.BASE_URL || 'https://localhost:7299/api/',
        },
    },
    modules: [
      '@nuxtjs/tailwindcss',
      "@primevue/nuxt-module",
      '@nuxt/icon',
      '@nuxtjs/google-fonts',
      '@nuxt/eslint'
    ],
    primevue: {
        options: {
            theme: {
                preset: JitenPreset,
                options: {
                    darkModeSelector: '.dark-mode',
                }
            }
        }
    },
    googleFonts: {
        families: {
            'Noto+Sans+JP': [400, 700],
        },
        display: 'swap',
    }
    ,
    css: ['~/assets/css/main.css']
})