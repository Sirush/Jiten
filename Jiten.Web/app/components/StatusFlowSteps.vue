<script setup lang="ts">
  import type { Deck } from '~/types';
  import type { useStatusFlow } from '~/composables/useStatusFlow';

  const props = defineProps<{
    deck: Deck;
    flow: ReturnType<typeof useStatusFlow>;
    backLabel?: string;
  }>();

  const emit = defineEmits<{ openHistory: [] }>();

  const step = computed(() => props.flow.step.value);
  const back = () => props.flow.goTo(props.flow.home);
  const stepBackLabel = computed(() => (props.flow.home === 'progress' ? 'Back to progress' : props.backLabel));
</script>

<template>
  <MediaListProgressStep
    v-if="step === 'progress'"
    :deck="deck"
    :show-back="flow.home === 'list'"
    @back="back"
    @done="flow.close()"
    @complete="flow.goTo('finished')"
    @open-history="emit('openHistory')"
  />
  <FinishDateChoice
    v-else-if="step === 'finished'"
    label="When did you finish?"
    :release-date="deck.releaseDate"
    :start-date="flow.passStart.value"
    :back-label="stepBackLabel"
    :disabled="flow.busy.value"
    @choose="flow.finish"
    @back="back"
  />
  <FinishDateChoice
    v-else-if="step === 'stopped'"
    label="When did you stop?"
    :start-date="flow.passStart.value"
    hide-release
    :back-label="stepBackLabel"
    :disabled="flow.busy.value"
    @choose="flow.stop"
    @back="back"
  />
  <PassChoice
    v-else-if="step === 'reread' || step === 'resume' || step === 'unfinish'"
    :kind="step"
    :media-type="deck.mediaType"
    :back-label="stepBackLabel"
    :disabled="flow.busy.value"
    @choose="flow.answer"
    @back="back"
  />
  <slot v-else />
</template>
