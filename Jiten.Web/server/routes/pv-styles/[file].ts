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

  // Unknown hash (stale HTML linking a pre-restart stylesheet): the current superset covers every
  // page this server has rendered, which includes the page that made this request. Short cache so
  // the placeholder content never gets pinned under a foreign hash.
  const fallback = currentPrimevueStylesheet().css;
  // An empty superset (worker that hasn't rendered yet) must never be cached as the answer.
  setHeader(event, 'Cache-Control', fallback ? 'public, max-age=300' : 'no-store');
  return fallback;
});
