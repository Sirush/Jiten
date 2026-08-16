<script setup lang="ts">
  const { tier, sources, fetched } = useJitenPlus();

  const PITCHES = [
    'richer cards with images and audio',
    'custom frequency lists',
    'a personalised immersion plan',
    'your coverage journey',
    'media request boosts',
  ];

  // Seeded through useState so the server-picked line survives hydration unchanged.
  const pitchIndex = useState('home-plus-pitch', () => Math.floor(Math.random() * PITCHES.length));

  // Lifetime wins over `plan`, which can still hold a stale value from a converted subscription.
  // Admin grants and full-tier promo credits have neither, so the plan suffix is simply omitted.
  const planLabel = computed(() => {
    const s = sources.value;
    if (!s) return null;
    if (s.isLifetime) return 'Lifetime';
    if (s.plan === 'Monthly') return 'Monthly';
    if (s.plan === 'Yearly') return 'Yearly';
    return null;
  });

  const fullLabel = computed(() => (planLabel.value ? `Jiten+ - ${planLabel.value}` : 'Jiten+'));

  const renewalLabel = computed(() => {
    const s = sources.value;
    if (!s || s.isLifetime || !s.subscriptionActive || !s.periodEnd) return null;
    const date = new Date(s.periodEnd).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
    return s.cancelAtPeriodEnd ? `Access until ${date}` : `Renews ${date}`;
  });

  const trialDaysLabel = computed(() => {
    const days = sources.value?.promoCreditDays ?? 0;
    return days === 1 ? '1 day left' : `${days} days left`;
  });
</script>

<template>
  <!-- Nothing until the tier is known, so a subscriber never sees the upsell flash first. -->
  <template v-if="fetched">
    <!-- Links out rather than listing the perks, so /jiten-plus stays the single source for them. -->
    <HomeStrip v-if="tier === 'full'" :label="fullLabel" icon="material-symbols-light:star" to="/jiten-plus">
      View your list of perks
      <span v-if="renewalLabel" class="text-surface-400 dark:text-surface-400">{{ renewalLabel }}</span>
    </HomeStrip>

    <HomeStrip v-else-if="tier === 'trial'" label="Jiten+ trial" icon="material-symbols-light:star" to="/jiten-plus">
      {{ trialDaysLabel }}
    </HomeStrip>

    <HomeStrip v-else label="Jiten+" icon="material-symbols-light:star" to="/jiten-plus"> Adds {{ PITCHES[pitchIndex] }}, and more. </HomeStrip>
  </template>
</template>
