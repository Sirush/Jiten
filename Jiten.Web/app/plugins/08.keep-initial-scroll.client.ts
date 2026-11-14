/** Nuxt scrolls to the top when the initial route resolves, which lands at hydration and undoes any scrolling done on the server-rendered page. */
export default defineNuxtPlugin((nuxtApp) => {
  useRouter().beforeResolve((to) => {
    if (nuxtApp.isHydrating && !to.hash) to.meta.scrollToTop = false;
  });
});
