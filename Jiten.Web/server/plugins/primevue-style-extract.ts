// PrimeVue styled mode inlines ~470KB of generated CSS as <style data-primevue-style-id> tags in
// every SSR response, with no official externalization mechanism (primefaces/primevue#7454/#8555,
// repo archived 2026-06). This moves that CSS into one hash-addressed, immutably-cacheable
// stylesheet served by /pv-styles/[file].ts.
export default defineNitroPlugin((nitroApp) => {
  // Prerendered HTML would link a hash no runtime server is guaranteed to have on boot.
  if (import.meta.prerender || process.env.PV_STYLE_EXTRACT === 'off') return;

  nitroApp.hooks.hook('render:html', async (html) => {
    const extracted = stripPrimevueStyles(html.head);
    if (extracted.length === 0) return;
    mergePrimevueStyles(extracted);
    const { hash } = currentPrimevueStylesheet();
    await primevueStylesheetPersisted();
    insertStylesheetLink(html.head, `/pv-styles/${hash}.css`);
  });
});
