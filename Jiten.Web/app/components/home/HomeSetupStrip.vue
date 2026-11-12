<script setup lang="ts">
  const { state, refresh } = useOnboarding();

  onMounted(() => refresh().catch(() => {}));

  const grammarMissing = computed(() => state.value?.basicGrammarMissing ?? 0);
  const knownWords = computed(() => state.value?.knownWords ?? 0);
</script>

<template>
  <HomeStrip v-if="state?.showSetupStrip" label="Your known words" icon="material-symbols:info-outline-rounded" to="/welcome" cta="Take the word check">
    <template v-if="knownWords > 0 && grammarMissing > 0">
      You know {{ knownWords.toLocaleString() }} words, but {{ grammarMissing.toLocaleString() }} basic grammar
      {{ grammarMissing === 1 ? "word isn't" : "words aren't" }} marked yet.
    </template>
    <template v-else>Tell Jiten what you already know to see your coverage of every title.</template>
  </HomeStrip>
</template>
