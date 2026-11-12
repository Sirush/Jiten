<script setup lang="ts">
  const emit = defineEmits<{ changed: []; back: []; continue: [] }>();

  type Source = 'anki-connect' | 'anki-file' | 'jpdb';
  const open = ref<Source | null>(null);
  const imported = ref<Set<Source>>(new Set());

  const sources: { key: Source; title: string; description: string }[] = [
    { key: 'anki-connect', title: 'Anki, through AnkiConnect', description: 'Reads your decks straight from Anki on this computer.' },
    { key: 'anki-file', title: 'Anki, from an exported file', description: 'Upload a .txt or .csv export of your notes.' },
    { key: 'jpdb', title: 'JPDB', description: 'Paste your JPDB API key and Jiten reads your decks.' },
  ];

  function onChanged(key: Source) {
    imported.value = new Set(imported.value).add(key);
    emit('changed');
  }
</script>

<template>
  <div class="flex flex-col gap-6">
    <div class="flex flex-col gap-1.5">
      <h1 class="text-2xl md:text-3xl font-bold">Import your flashcards</h1>
      <p class="text-gray-600 dark:text-gray-300">
        If you're coming over from Anki or JPDB, you can import your knowledge here. You can also do it at anytime from your vocabulary settings.
      </p>
    </div>

    <section class="rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 divide-y divide-gray-100 dark:divide-neutral-800">
      <div v-for="source in sources" :key="source.key">
        <button
          type="button"
          class="w-full flex items-center gap-4 px-4 py-4 sm:px-5 text-left focus-visible:outline-2 focus-visible:outline-primary-500"
          :aria-expanded="open === source.key"
          @click="open = open === source.key ? null : source.key"
        >
          <span class="w-10 h-10 rounded-lg shrink-0 flex items-center justify-center bg-primary-50 dark:bg-primary-900/40 text-primary-600 dark:text-primary-300">
            <Icon name="material-symbols:upload-rounded" size="1.4em" />
          </span>
          <span class="flex-1 min-w-0 flex flex-col gap-0.5">
            <span class="font-bold">{{ source.title }}</span>
            <span class="text-sm text-gray-600 dark:text-gray-300">{{ source.description }}</span>
            <span v-if="imported.has(source.key)" class="flex items-center gap-1 text-[13px] font-semibold text-green-700 dark:text-green-400">
              <Icon name="material-symbols:check-rounded" /> Imported
            </span>
          </span>
          <Icon
            name="material-symbols:expand-more-rounded"
            size="1.5em"
            class="shrink-0 text-gray-500 transition-transform"
            :class="open === source.key ? 'rotate-180' : ''"
          />
        </button>
        <div v-if="open === source.key" class="px-2 pb-4 sm:px-5">
          <VocabularyImportAnkiConnectPanel v-if="source.key === 'anki-connect'" @changed="onChanged(source.key)" />
          <VocabularyImportAnkiFilePanel v-else-if="source.key === 'anki-file'" @changed="onChanged(source.key)" />
          <VocabularyImportJpdbPanel v-else @changed="onChanged(source.key)" />
        </div>
      </div>
    </section>

    <div class="flex items-center justify-between gap-4">
      <Button label="Back" severity="secondary" outlined @click="emit('back')" />
      <div class="flex items-center gap-4">
        <Button v-if="imported.size === 0" label="I don't use these" link class="!px-0" @click="emit('continue')" />
        <Button label="Continue" @click="emit('continue')" />
      </div>
    </div>
  </div>
</template>
