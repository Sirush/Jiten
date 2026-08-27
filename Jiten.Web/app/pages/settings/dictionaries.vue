<script setup lang="ts">
  useHead({ title: 'Dictionaries - Settings - Jiten' });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 flex flex-col gap-4">
    <div class="flex items-center gap-2">
      <NuxtLink to="/settings">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded />
      </NuxtLink>
      <h1 class="text-2xl font-bold">Dictionaries</h1>
    </div>

    <Card>
      <template #content>
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-4">
          Manage your custom dictionaries. Definitions from your dictionaries appear on vocabulary pages and in downloaded Anki decks.
          <br />
          Dictionary data is stored locally in your browser and is never uploaded to the server.
        </p>
        <DictionaryManager />
        <div class="mt-4 pt-4 border-t border-surface-200 dark:border-surface-700">
          <DictionaryFontSizeControl :show-label="true" />
        </div>
      </template>
    </Card>

    <Card>
      <template #title>
        <h3 class="text-lg font-semibold">How it works</h3>
      </template>
      <template #content>
        <ol class="list-decimal list-inside text-sm text-gray-600 dark:text-gray-300 space-y-2">
          <li>JMDict is always present as a built-in dictionary and cannot be removed</li>
          <li>Import additional Yomitan dictionaries (.zip files) to add custom definitions</li>
          <li>Drag dictionaries to reorder. Dictionaries higher in the list will be displayed first</li>
          <li>
            Choose a mode per dictionary:
            <ul class="list-disc list-inside ml-4 mt-1 text-xs text-gray-500 dark:text-gray-400">
              <li>
                <strong>Always show</strong>
                — definitions from this dictionary are always displayed when a match is found
              </li>
              <li>
                <strong>Fallback</strong>
                — definitions are only shown when no "always show" dictionary has a match for that word
              </li>
              <li>
                <strong>Disabled</strong>
                — this dictionary data will never be shown anywhere
              </li>
            </ul>
          </li>
          <li>Definitions appear on vocabulary pages and are applied to Anki deck downloads automatically when you check the corresponding option</li>
        </ol>
      </template>
    </Card>
  </div>
</template>
