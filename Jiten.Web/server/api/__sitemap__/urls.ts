import { defineEventHandler } from 'h3';
import type { SitemapUrl } from '@nuxtjs/sitemap/dist/runtime/types';
import { MediaType } from '~/types/enums';
import { getMediaTypeSlug } from '~/utils/mediaTypeMapper';

// /decks/media/list/{slug} hub pages, derived from the MediaType enum so new types appear automatically.
const MEDIA_TYPE_SLUGS = Object.values(MediaType)
  .filter((v): v is number => typeof v === 'number')
  .map((t) => getMediaTypeSlug(t));

export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = config.public.baseURL;
  const urls: SitemapUrl[] = [];

  for (const slug of MEDIA_TYPE_SLUGS) {
    urls.push({ loc: `/decks/media/list/${slug}`, changefreq: 'daily', priority: 0.6, _sitemap: 'pages' });
  }

  try {
    const guides = await queryCollection(event, 'guides').where('draft', '=', false).select('path', 'updated').all();
    for (const g of guides) {
      urls.push({
        loc: g.path,
        lastmod: g.updated ? new Date(g.updated).toISOString() : undefined,
        changefreq: 'monthly',
        priority: 0.6,
        _sitemap: 'pages',
      });
    }
  } catch (e) {
    console.error('Error fetching guides for sitemap:', e);
  }

  // Deck detail + kanji pages come from two independent API calls — fetch them concurrently.
  const [decksResult, kanjiResult] = await Promise.allSettled([
    $fetch<{ id: number; lastUpdate: string; coverName: string }[]>(`${base}media-deck/get-media-decks-sitemap`),
    $fetch<string[]>(`${base}kanji/sitemap-characters`),
  ]);

  if (decksResult.status === 'fulfilled') {
    for (const d of decksResult.value) {
      urls.push({
        loc: `/decks/media/${d.id}/detail`,
        lastmod: d.lastUpdate,
        images: d.coverName && d.coverName !== 'nocover.jpg' ? [{ loc: d.coverName }] : undefined,
        changefreq: 'weekly',
        priority: 0.8,
        _sitemap: 'pages',
      });
    }
  } else {
    console.error('Error fetching deck sitemap data:', decksResult.reason);
  }

  // Kanji pages (corpus kanji appearing in >=10 distinct words).
  if (kanjiResult.status === 'fulfilled') {
    for (const c of kanjiResult.value) {
      urls.push({ loc: `/kanji/${c}`, changefreq: 'monthly', priority: 0.5, _sitemap: 'pages' });
    }
  } else {
    console.error('Error fetching kanji sitemap data:', kanjiResult.reason);
  }

  return urls;
});
