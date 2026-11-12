<script setup lang="ts">
  import { WORD_CHECK_LEVEL_TITLES } from '~/types/wordCheck';

  const { state, refresh } = useOnboarding();
  const failed = ref(false);

  onMounted(() => refresh().catch(() => (failed.value = true)));
</script>

<template>
  <SettingsListItem v-if="!failed" title="Word & Reading Checks" description="Estimate your known words with two short quizzes.">
    <template #action>
      <NuxtLink to="/welcome" class="text-sm font-medium">Open Setup</NuxtLink>
    </template>
    <template #default>
      <div v-if="state" class="grid grid-cols-1 md:grid-cols-2 gap-y-5 md:gap-x-8">
        <section class="flex flex-col gap-2.5">
          <h3 class="font-semibold">Word check</h3>
          <dl class="flex flex-col gap-1.5 text-sm">
            <div class="flex justify-between gap-3">
              <dt class="text-muted-color">Level</dt>
              <dd class="font-semibold">{{ state.level ? WORD_CHECK_LEVEL_TITLES[state.level] : 'Not set' }}</dd>
            </div>
            <div class="flex justify-between gap-3">
              <dt class="text-muted-color">{{ state.wordCheckDoneAt ? 'Words added' : 'Status' }}</dt>
              <dd class="font-semibold tabular-nums">{{ state.wordCheckDoneAt ? (state.wordCheckWords ?? 0).toLocaleString() : 'Not taken yet' }}</dd>
            </div>
            <div v-if="state.wordCheckDoneAt" class="flex justify-between gap-3">
              <dt class="text-muted-color">Last taken</dt>
              <dd class="font-semibold">{{ formatDateShort(state.wordCheckDoneAt) }}</dd>
            </div>
          </dl>
          <NuxtLink
            :to="state.level ? `/welcome?step=check${state.wordCheckDoneAt ? '&retake=1' : ''}` : '/welcome'"
            class="p-button p-button-sm self-start no-underline mt-auto"
            :class="state.wordCheckDoneAt ? 'p-button-secondary p-button-outlined' : ''"
            :aria-label="state.wordCheckDoneAt ? 'Retake word check' : 'Start word check'"
          >
            {{ state.wordCheckDoneAt ? 'Retake' : 'Start' }}
          </NuxtLink>
        </section>

        <section class="flex flex-col gap-2.5 border-t border-surface-200 dark:border-surface-700 pt-5 md:border-t-0 md:border-l md:pt-0 md:pl-8">
          <h3 class="font-semibold">Reading check</h3>
          <template v-if="state.readingCheck">
            <dl class="flex flex-col gap-1.5 text-sm">
              <div class="flex justify-between gap-3">
                <dt class="text-muted-color">Volumes and works</dt>
                <dd class="font-semibold tabular-nums">{{ state.readingCheck.units }}</dd>
              </div>
              <div class="flex justify-between gap-3">
                <dt class="text-muted-color">Words added</dt>
                <dd class="font-semibold tabular-nums">{{ state.readingCheck.seeded.toLocaleString() }}</dd>
              </div>
              <div class="flex justify-between gap-3">
                <dt class="text-muted-color">Last taken</dt>
                <dd class="font-semibold">{{ formatDateShort(state.readingCheck.finishedAt) }}</dd>
              </div>
            </dl>
            <p v-if="state.readingCheck.retentionEncounters" class="text-sm text-muted-color">
              You tend to know a word once you've met it about {{ state.readingCheck.retentionEncounters }} times.
            </p>
          </template>
          <p v-else class="text-sm text-muted-color">
            Take a short quiz on words from titles you've finished to help fill in a part of your knowledge accurately.
          </p>
          <NuxtLink
            to="/welcome?step=read"
            class="p-button p-button-sm self-start no-underline mt-auto"
            :class="state.readingCheck ? 'p-button-secondary p-button-outlined' : ''"
            :aria-label="state.readingCheck ? 'Retake reading check' : 'Start reading check'"
          >
            {{ state.readingCheck ? 'Retake' : 'Start' }}
          </NuxtLink>
        </section>
      </div>
      <Skeleton v-else height="7rem" />
    </template>
  </SettingsListItem>
</template>
