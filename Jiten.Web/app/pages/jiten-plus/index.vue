<script setup lang="ts">
  import type { JitenPlusPricingInfo } from '~/types';
  import { useToast } from 'primevue/usetoast';
  import { useAuthStore } from '~/stores/authStore';

  const auth = useAuthStore();
  const toast = useToast();
  const route = useRoute();
  const router = useRouter();

  const { isPlus, isTrial, sources, loading, fetched } = useJitenPlus();
  const isLifetime = computed(() => !!sources.value?.isLifetime);

  const { data: pricing } = useApiFetch<JitenPlusPricingInfo>('/jiten-plus/pricing');

  const showDashboard = computed(() => auth.isAuthenticated && isPlus.value);
  // The tier is fetched client-side only; holding the page behind a spinner until it resolves
  // stops members from seeing the marketing page flash before the dashboard swaps in.
  const resolving = computed(() => auth.isAuthenticated && !fetched.value && (loading.value || import.meta.server));

  const upgradeHeading = computed(() => (isTrial.value ? 'Upgrade to a paid plan' : 'Plans'));
  const upgradeSubtitle = computed(() => {
    if (isTrial.value) return 'Keep every feature after your trial ends, with full storage and saved frequency lists.';
    if (pricing.value?.lifetimeAvailable) return 'Moving to lifetime? Your prepaid subscription time is credited toward it.';
    return 'Switch plans at any time.';
  });

  const lifetimeAvailable = computed(() => pricing.value?.lifetimeAvailable ?? true);
  const lifetimeWindowEndLabel = computed(() => {
    const raw = pricing.value?.lifetimeWindowEnd;
    if (!raw) return null;
    const d = new Date(raw);
    if (Number.isNaN(d.getTime())) return null;
    return d.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  });

  const fullStorageLabel = computed(() => {
    const bytes = pricing.value?.cardMediaStorage?.fullBytes;
    return bytes ? formatBytes(bytes) : null;
  });

  const limitRows = computed(() => {
    const l = pricing.value?.limits;
    if (!l) return [];
    return [
      { label: 'Study decks', ...l.studyDecks },
      { label: 'Words across word list decks', ...l.studyDeckWords },
      { label: 'Words per import', ...l.importWords },
      { label: 'Active media requests', ...l.activeMediaRequests },
      { label: 'Custom sentences per word', ...l.customSentencesPerWord },
    ].map((row) => ({ label: row.label, free: row.free.toLocaleString(), plus: row.plus.toLocaleString() }));
  });

  const showLifetimeNotice = computed(() => lifetimeAvailable.value && !!lifetimeWindowEndLabel.value && !isLifetime.value);

  const exampleJourney = computed(() => buildExampleJourney());
  const exampleRange = computed(() => {
    const points = exampleJourney.value.points;
    return {
      start: formatBucketDated(points[0]!.date, 'monthly'),
      end: formatBucketDated(points[points.length - 1]!.date, 'monthly'),
    };
  });

  onMounted(() => {
    if (route.query.checkout === 'cancelled') {
      toast.add({
        severity: 'info',
        summary: 'Checkout cancelled',
        detail: "You haven't been charged.",
        life: 5000,
      });
      const query = { ...route.query };
      delete query.checkout;
      router.replace({ query });
    }
  });

  useSeoMeta({
    title: 'Jiten+ - Get more from Jiten and help it grow',
    description:
      'Jiten+ adds richer cards, custom frequency lists, a personalised immersion plan, media request boosts, your coverage journey, higher limits and more while helping support Jiten. Everything free stays free.',
    ogTitle: 'Jiten+ - Get more from Jiten and help it grow',
    ogDescription: 'Get useful extras while helping support Jiten. Everything free stays free.',
    ogType: 'website',
    twitterCard: 'summary_large_image',
  });

  defineOgImage('PageOgImage', {
    title: 'Jiten+',
    description: 'Get useful extras while helping support Jiten. Everything free stays free.',
  });

  const faq = [
    {
      q: 'Can I cancel a subscription anytime?',
      a: "Yes. Monthly and yearly subscriptions can be cancelled at any time. You'll keep Jiten+ until the end of your current billing period.",
    },
    {
      q: 'What happens to my data if I cancel?',
      a: "Your existing uploads will remain available. You simply won't be able to add new uploads until you resubscribe.",
    },
    {
      q: 'What if prices go up later?',
      a: "Your subscription price is locked in. As long as your subscription remains active, you'll keep the price you originally signed up for.",
    },
    {
      q: 'Does supporting on Patreon or Ko-fi include Jiten+?',
      a: "No. Patreon and Ko-fi contributions are treated as donations and don't include Jiten+. They're available for anyone who would like to provide additional support for Jiten's development.",
    },
    {
      q: 'How does the lifetime offer work?',
      a: "Lifetime access is available during limited offer periods, which may return from time to time. If you're already subscribed, your prepaid subscription time is credited toward the lifetime price. You pay once and keep every Jiten+ benefit for the lifetime of the service, with no renewals or additional subscription payments.",
    },
  ];
</script>

<template>
  <div class="py-6 md:py-10">
    <div v-if="resolving" class="flex justify-center items-center py-24">
      <ProgressSpinner style="width: 44px; height: 44px" stroke-width="4" />
    </div>

    <template v-else>
      <!-- Member dashboard -->
      <template v-if="showDashboard">
        <JitenPlusDashboard />

        <section v-if="!isLifetime" class="mt-14">
          <h2 class="text-2xl font-bold text-center mb-2 text-gray-900 dark:text-white">{{ upgradeHeading }}</h2>
          <p class="text-center text-gray-600 dark:text-gray-300 mb-8">{{ upgradeSubtitle }}</p>
          <JitenPlusPricingCards :pricing="pricing" />
        </section>
      </template>

      <!-- Marketing page -->
      <template v-else>
        <!-- Hero -->
        <section class="text-center max-w-3xl mx-auto px-2">
          <JitenPlusBadge :link="false" class="!text-sm !px-3 !py-1 mb-4" />
          <h1 class="text-3xl md:text-4xl font-bold mb-4 text-gray-900 dark:text-white">Get more from Jiten and help it grow</h1>
          <p class="text-lg text-gray-600 dark:text-gray-300 leading-relaxed">
            <span class="font-semibold text-gray-800 dark:text-gray-100">Jiten+</span> gives you extra tools to personalize your learning while helping cover
            server costs and keep Jiten growing. Everything that's free today stays free, forever.
          </p>
          <ul class="mt-5 flex flex-wrap items-center justify-center gap-y-2.5 gap-x-6 text-sm font-medium text-gray-700 dark:text-gray-200">
            <li class="inline-flex items-center gap-1.5">
              <Icon name="material-symbols:check-circle-rounded" class="text-primary-500" />
              Richer cards with images &amp; audio
            </li>
            <li class="inline-flex items-center gap-1.5">
              <Icon name="material-symbols:check-circle-rounded" class="text-primary-500" />
              Custom frequency lists
            </li>
            <li class="inline-flex items-center gap-1.5">
              <Icon name="material-symbols:check-circle-rounded" class="text-primary-500" />
              Personalised immersion plans
            </li>
            <li class="inline-flex items-center gap-1.5">
              <Icon name="material-symbols:check-circle-rounded" class="text-primary-500" />
              Your coverage journey
            </li>
            <li class="inline-flex items-center gap-1.5">
              <Icon name="material-symbols:check-circle-rounded" class="text-primary-500" />
              Higher limits &amp; monthly boosts
            </li>
          </ul>
          <div v-if="showLifetimeNotice" class="mt-5">
            <a
              href="#pricing"
              class="inline-flex items-center gap-2 rounded-lg bg-amber-50 dark:bg-amber-950/40 px-4 py-2 hover:bg-amber-100 dark:hover:bg-amber-950/60 transition-colors"
            >
              <Icon name="material-symbols:event-available-outline-rounded" class="text-amber-600 dark:text-amber-400" />
              <span class="text-sm font-medium text-amber-800 dark:text-amber-300">Lifetime access is available until {{ lifetimeWindowEndLabel }}.</span>
            </a>
          </div>
        </section>

        <!-- Pricing cards -->
        <div class="mt-10">
          <JitenPlusPricingCards :pricing="pricing" />
        </div>

        <!-- Personal note -->
        <section class="mt-10 max-w-3xl mx-auto text-center border-y border-gray-200 dark:border-gray-800 py-7">
          <h2 class="jp-note__title text-gray-900 dark:text-white">Made by one person</h2>
          <p class="jp-note__body text-gray-600 dark:text-gray-300">
            Jiten is built and maintained by me alone. It's not a side project: I have been working on it full time for over a year now, and have spent
            thousands of hours on the parser, the decks and everything in between. I am very grateful for all the contributions and donations, but they don't
            cover a salary yet, and Jiten+ is what gets it there, without putting anything that's currently free behind a paywall.
          </p>
          <p class="jp-note__sign">
            <span class="jp-note__name text-gray-800 dark:text-gray-100">Sirus</span>
            <span class="jp-note__role text-gray-500 dark:text-gray-400">Creator of Jiten</span>
          </p>
        </section>

        <!-- What you get -->
        <section class="mt-14 max-w-4xl mx-auto">
          <h2 class="text-2xl font-bold text-center mb-2 text-gray-900 dark:text-white">Everything included with Jiten+</h2>
          <p class="text-center text-gray-600 dark:text-gray-300 mb-6">Jiten's core features stay free. Jiten+ adds these extras on top.</p>
          <div class="grid gap-4 sm:grid-cols-2">
            <div class="jp-feature border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
              <div class="jp-feature__head">
                <Icon name="material-symbols:image-outline-rounded" class="jp-feature__icon" />
                <h3 class="jp-feature__title text-gray-900 dark:text-white">Richer cards with images &amp; audio</h3>
              </div>
              <p class="jp-feature__body text-gray-600 dark:text-gray-300">
                Make your cards more memorable with your own images and audio.
                <template v-if="fullStorageLabel">You get {{ fullStorageLabel }} of storage, and you</template>
                <template v-else>You</template>
                keep your uploads even if you cancel.
              </p>
            </div>

            <div class="jp-feature border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
              <div class="jp-feature__head">
                <Icon name="material-symbols:format-list-numbered-rounded" class="jp-feature__icon" />
                <h3 class="jp-feature__title text-gray-900 dark:text-white">Frequency lists made for you</h3>
              </div>
              <p class="jp-feature__body text-gray-600 dark:text-gray-300">
                Build frequency lists from any media available on Jiten, use them in Yomitan, and share them with a link. Saved lists update automatically as
                Jiten grows.
              </p>
            </div>

            <div class="jp-feature border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
              <div class="jp-feature__head">
                <Icon name="material-symbols:explore-rounded" class="jp-feature__icon" />
                <h3 class="jp-feature__title text-gray-900 dark:text-white">Immersion plans</h3>
              </div>
              <p class="jp-feature__body text-gray-600 dark:text-gray-300">
                Find what to immerse in next or forge a path towards a title you really care about. The algorithm will try to find the ideal picks according to
                your preferences.
              </p>
            </div>

            <div class="jp-feature border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
              <div class="jp-feature__head">
                <Icon name="material-symbols:bolt-rounded" class="jp-feature__icon" />
                <h3 class="jp-feature__title text-gray-900 dark:text-white">Media request boosts</h3>
              </div>
              <p class="jp-feature__body text-gray-600 dark:text-gray-300">
                Get 5 boosts every month to prioritize any open media request. Each boost is the equivalent of 5 regular votes.
              </p>
            </div>

            <div class="jp-feature sm:col-span-2 border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
              <div class="grid gap-4">
                <div class="text-center">
                  <div class="jp-feature__head justify-center">
                    <Icon name="material-symbols:show-chart-rounded" class="jp-feature__icon" />
                    <h3 class="jp-feature__title text-gray-900 dark:text-white">Your coverage journey</h3>
                  </div>
                  <p class="jp-feature__body text-gray-600 dark:text-gray-300 max-w-2xl mx-auto">
                    Watch how the coverage of the titles you're interested in grows over time. Get a look back at the journey that got you where you are today.
                  </p>
                </div>
                <div class="rounded-lg bg-gray-50 dark:bg-gray-800/60 p-3">
                  <div class="flex items-baseline justify-between gap-2 mb-1">
                    <span class="text-sm font-semibold">
                      <span class="text-gray-500 dark:text-gray-400">{{ exampleJourney.startCoverage.toFixed(0) }}%</span>
                      <span class="text-gray-400 dark:text-gray-400 mx-1">&rarr;</span>
                      <span class="text-primary-600 dark:text-primary-300">{{ exampleJourney.currentCoverage.toFixed(0) }}% readable</span>
                    </span>
                    <span class="text-[10px] uppercase tracking-wide text-gray-400 dark:text-gray-400">Example</span>
                  </div>
                  <LazyCoverageJourneyChart :points="exampleJourney.points" granularity="monthly" compact :tooltip="false" height="150px" hydrate-on-visible />
                  <div class="flex justify-between text-[10px] text-gray-400 dark:text-gray-400 mt-0.5">
                    <span>{{ exampleRange.start }}</span>
                    <span>{{ exampleRange.end }}</span>
                  </div>
                </div>
              </div>
            </div>

            <div v-if="limitRows.length" class="jp-feature sm:col-span-2 border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
              <div class="jp-feature__head">
                <Icon name="material-symbols:trending-up-rounded" class="jp-feature__icon" />
                <h3 class="jp-feature__title text-gray-900 dark:text-white">Higher limits</h3>
              </div>
              <p class="jp-feature__body text-gray-600 dark:text-gray-300">Free limits cover most learners. If you're a power user, Jiten+ raises the caps.</p>
              <div class="mt-3 overflow-x-auto">
                <table class="w-full text-sm border-collapse">
                  <thead>
                    <tr class="text-left text-gray-500 dark:text-gray-400">
                      <th class="font-medium py-1 pr-3" />
                      <th class="font-medium py-1 px-3 text-right whitespace-nowrap">Free</th>
                      <th class="font-medium py-1 pl-3 text-right whitespace-nowrap">Jiten+</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="row in limitRows" :key="row.label" class="border-t border-gray-100 dark:border-gray-800">
                      <td class="py-1.5 pr-3 text-gray-700 dark:text-gray-300">{{ row.label }}</td>
                      <td class="py-1.5 px-3 text-right tabular-nums text-gray-500 dark:text-gray-400">{{ row.free }}</td>
                      <td class="py-1.5 pl-3 text-right tabular-nums font-semibold text-primary-600 dark:text-primary-300">{{ row.plus }}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </div>
          </div>
        </section>
      </template>

      <!-- FAQ -->
      <section class="mt-12 max-w-3xl mx-auto">
        <h2 class="text-2xl font-bold text-center mb-6 text-gray-900 dark:text-white">Questions</h2>
        <div class="space-y-2">
          <details v-for="item in faq" :key="item.q" class="jp-faq border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
            <summary class="jp-faq__q text-gray-800 dark:text-gray-100">{{ item.q }}</summary>
            <p class="jp-faq__a text-gray-600 dark:text-gray-300">{{ item.a }}</p>
          </details>
        </div>
      </section>
    </template>
  </div>
</template>

<style scoped>
  .jp-feature {
    border-radius: var(--radius-xl);
    padding: 1.1rem;
  }

  .jp-feature__head {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .jp-feature__icon {
    font-size: 1.3rem;
    color: var(--p-primary-500);
  }

  .jp-feature__title {
    font-weight: 600;
  }

  .jp-feature__body {
    margin-top: 0.5rem;
    font-size: 0.85rem;
    line-height: 1.5;
  }

  .jp-note__title {
    font-size: 1.35rem;
    font-weight: 700;
  }

  .jp-note__body {
    margin-top: 0.75rem;
    font-size: 0.95rem;
    line-height: 1.75;
  }

  .jp-note__sign {
    display: flex;
    flex-direction: column;
    align-items: center;
    margin-top: 1.1rem;
    line-height: 1.3;
  }

  .jp-note__name {
    font-weight: 600;
    font-size: 0.9rem;
  }

  .jp-note__role {
    font-size: 0.75rem;
  }

  .jp-faq {
    border-radius: var(--radius-lg);
    padding: 0.85rem 1rem;
  }

  .jp-faq__q {
    cursor: pointer;
    font-weight: 600;
    list-style: none;
  }

  .jp-faq__q::-webkit-details-marker {
    display: none;
  }

  .jp-faq__q::before {
    content: '＋';
    display: inline-block;
    width: 1.1rem;
    color: var(--p-primary-500);
    font-weight: 700;
  }

  .jp-faq[open] .jp-faq__q::before {
    content: '－';
  }

  .jp-faq__a {
    margin-top: 0.6rem;
    padding-left: 1.1rem;
    font-size: 0.87rem;
    line-height: 1.55;
  }
</style>
