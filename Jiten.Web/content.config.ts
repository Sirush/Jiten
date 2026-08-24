import { defineContentConfig, defineCollection, z } from '@nuxt/content';

// Guides: markdown-in-repo editorial content (tutorials + folded-in FAQ).
// Slugs are a public contract — filename = slug. See PLAN_Guides.md.
export default defineContentConfig({
  collections: {
    guides: defineCollection({
      type: 'page',
      source: 'guides/**/*.md',
      schema: z.object({
        title: z.string(),
        seoTitle: z.string().optional(),
        summary: z.string(),
        // Drives the index nav grouping + order. Keep in sync with CATEGORY_ORDER in the index page.
        category: z.enum(['Getting Started', 'Using Jiten', 'Studying', 'Coming from another app?', 'Advanced & tools', 'FAQ']),
        level: z.enum(['beginner', 'advanced']).default('beginner'),
        order: z.number().default(100),
        icon: z.string().optional(),
        updated: z.date().optional(),
        published: z.date().optional(),
        verified: z.date().optional(),
        draft: z.boolean().default(false),
      }),
    }),
  },
});
