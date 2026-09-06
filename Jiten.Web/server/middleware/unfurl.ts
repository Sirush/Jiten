const UNFURL_BOTS =
  /(Discordbot|Twitterbot|Slackbot|TelegramBot|facebookexternalhit|redditbot|WhatsApp|LinkedInBot|Mastodon|Bluesky|Applebot|Embedly|Iframely|SkypeUriPreview|Pinterestbot)/i;
const SKIP_PREFIXES = ['/_nuxt', '/__nuxt', '/api/', '/healthz', '/_scripts', '/_fonts', '/_ipx', '/__og-image__'];
const SKIP_EXTENSIONS = /\.(?:js|mjs|css|map|png|jpe?g|svg|webp|ico|woff2?|ttf|wasm|txt|xml|json)$/i;

export default defineEventHandler((event) => {
  if (event.method !== 'GET') return;
  const path = (event.path || '').split('?')[0] ?? '';
  if (SKIP_PREFIXES.some((p) => path.startsWith(p)) || SKIP_EXTENSIONS.test(path)) return;

  const match = UNFURL_BOTS.exec(String(getRequestHeader(event, 'user-agent') ?? ''));
  if (!match) return;

  const host = String(getRequestHeader(event, 'host') ?? '').split(':')[0] ?? '';
  relayToKami(event, JSON.stringify({ s: host, e: [{ t: 'bot', n: match[1], p: path, dt: 0 }] }));
});
