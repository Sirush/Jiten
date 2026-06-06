// server/api/__sitemap__/urls.ts
import { defineEventHandler } from 'h3'
import type { SitemapUrl } from '@nuxtjs/sitemap/dist/runtime/types'
import { MediaType } from '~/types/enums'

// /decks/media/list/{n} hub pages, derived from the MediaType enum so new types appear automatically.
const MEDIA_TYPE_IDS = Object.values(MediaType).filter((v): v is number => typeof v === 'number')

export default defineEventHandler(async () => {
    const config = useRuntimeConfig()
    const base = config.public.baseURL
    const urls: SitemapUrl[] = []

    // Media-type list hub pages (static set of routes).
    for (const t of MEDIA_TYPE_IDS) {
        urls.push({ loc: `/decks/media/list/${t}`, changefreq: 'daily', priority: 0.6, _sitemap: 'pages' })
    }

    // Deck detail + kanji pages come from two independent API calls — fetch them concurrently.
    const [decksResult, kanjiResult] = await Promise.allSettled([
        $fetch<{ id: number; lastUpdate: string; coverName: string }[]>(`${base}media-deck/get-media-decks-sitemap`),
        $fetch<string[]>(`${base}kanji/sitemap-characters`),
    ])

    // Deck detail pages, enriched with lastmod (freshness signal) and the cover image.
    if (decksResult.status === 'fulfilled') {
        for (const d of decksResult.value) {
            urls.push({
                loc: `/decks/media/${d.id}/detail`,
                lastmod: d.lastUpdate,
                images: d.coverName && d.coverName !== 'nocover.jpg' ? [{ loc: d.coverName }] : undefined,
                changefreq: 'weekly',
                priority: 0.8,
                _sitemap: 'pages',
            })
        }
    } else {
        console.error('Error fetching deck sitemap data:', decksResult.reason)
    }

    // Kanji pages (corpus kanji appearing in >=10 distinct words).
    if (kanjiResult.status === 'fulfilled') {
        for (const c of kanjiResult.value) {
            urls.push({ loc: `/kanji/${encodeURIComponent(c)}`, changefreq: 'monthly', priority: 0.5, _sitemap: 'pages' })
        }
    } else {
        console.error('Error fetching kanji sitemap data:', kanjiResult.reason)
    }

    return urls
})
