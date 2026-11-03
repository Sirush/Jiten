<script setup lang="ts">
  const visible = defineModel<boolean>('visible', { required: true });

  const RULES = [
    'Make sure every change is factual.',
    'For translated works, use the date of the Japanese edition release, not the original one.',
    'Do not use MTL or AI translation anywhere. Do not translate titles in "English title" if they don\'t have an official release under that name',
    'Add a source for changes: big databases like VNDB, AniList or Wikipedia, the publisher site, a trustworthy news srource...',
    'No spoilers anywhere: tags, aliases and descriptions included.',
    'Aliases are alternative names or spellings used for search: official titles, common abbreviations, translations. No jokes and no links.',
    'Related decks are for works in the same story: sequels, side stories, adaptations. Not works that only share an author or publisher.',
    'Your edits are public and show your display name.',
    'Trolling, spam or deliberately wrong edits will cost you the right to suggest edits permanently.',
  ];
</script>

<template>
  <Dialog v-model:visible="visible" modal header="Rules for suggesting edits" class="w-full" style="max-width: 36rem" :dismissable-mask="true">
    <ul class="m-0 pl-5 flex flex-col gap-2 list-disc text-sm leading-relaxed">
      <li v-for="rule in RULES" :key="rule">{{ rule }}</li>
    </ul>
    <template #footer>
      <Button label="Close" @click="visible = false" />
    </template>
  </Dialog>
</template>
