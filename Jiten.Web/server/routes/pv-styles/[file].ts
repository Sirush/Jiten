export default defineEventHandler(async (event) => {
  const file = getRouterParam(event, 'file') || '';
  const match = /^([0-9a-f]{12})\.css$/.exec(file);
  if (!match) throw createError({ statusCode: 404 });

  setHeader(event, 'Content-Type', 'text/css; charset=utf-8');

  const css = await readPrimevueStylesheet(match[1]!);
  if (css !== null) {
    setHeader(event, 'Cache-Control', 'public, max-age=31536000, immutable');
    return css;
  }

  /** An unknown hash redirects to the current stylesheet and is never answered in place: the CDN keeps every 200 for a year, and this server can link that same content-derived hash again later. */
  setHeader(event, 'Cache-Control', 'no-store');
  const { css: fallback, hash } = currentPrimevueStylesheet();
  if (!fallback) throw createError({ statusCode: 503 });
  return sendRedirect(event, `/pv-styles/${hash}.css`, 307);
});
