import { stopTts } from '~/composables/useTts';

export default defineNuxtPlugin(() => {
  const router = useRouter();
  router.beforeEach(() => {
    stopTts();
  });
});
