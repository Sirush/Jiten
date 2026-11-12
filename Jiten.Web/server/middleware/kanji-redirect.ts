export default defineEventHandler((event) => {
  if (event.method !== 'GET' && event.method !== 'HEAD') return;

  const [path = '', query] = (event.path || '').split('?');
  if (!path.startsWith('/kanji/%25')) return;

  const target = singleEncodedKanjiPath(path);
  if (target) return sendRedirect(event, query ? `${target}?${query}` : target, 301);
});
