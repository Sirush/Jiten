<script setup lang="ts">
  const emit = defineEmits<{ changed: []; back: []; continue: [] }>();

  const running = ref<number[] | null>(null);
  const finished = ref(false);

  function onDone() {
    finished.value = true;
    emit('changed');
  }
</script>

<template>
  <div class="flex flex-col gap-6">
    <div class="flex flex-col gap-1.5">
      <h1 class="text-2xl md:text-3xl font-bold">Add words from your immersion</h1>
      <p class="text-gray-600 dark:text-gray-300">
        In this step, you'll be asked if you know the words you've seen multiple times during your immersion.
      </p>
    </div>

    <section
      v-show="!running"
      class="rounded-xl border-2 border-primary-600 dark:border-primary-400 bg-white dark:bg-neutral-900 p-5 md:p-6 flex flex-col gap-4"
    >
      <div class="flex flex-col gap-2">
        <h2 class="text-xl font-bold">Check the words from titles you finished</h2>
        <p class="text-sm text-gray-700 dark:text-gray-300">
          Select all the titles you've finished, whether it's a few volumes/episode or the whole work. You'll then be quizzed on the words that appear in those. This will take 5 to 10 minutes.
        </p>
      </div>
      <ReadingCheckPicker @start="running = $event" />
    </section>

    <ReadingCheckRunner v-if="running" :deck-ids="running" @done="onDone" @cancel="running = null" />

    <p v-if="!running" class="flex items-start gap-2.5 text-sm text-gray-700 dark:text-gray-300">
      <Icon name="material-symbols:info-outline-rounded" size="1.25em" class="shrink-0 text-primary-700 dark:text-primary-300 mt-px" />
      Haven't finished anything in Japanese yet? Skip this step. You can run it later from your vocabulary settings once you finish a few titles.
    </p>

    <div v-if="!running || finished" class="flex items-center justify-between gap-4">
      <Button label="Back" severity="secondary" outlined @click="emit('back')" />
      <div class="flex items-center gap-4">
        <Button v-if="finished" label="Continue" @click="emit('continue')" />
        <Button v-else label="Skip for now" link class="!px-0" @click="emit('continue')" />
      </div>
    </div>
  </div>
</template>
