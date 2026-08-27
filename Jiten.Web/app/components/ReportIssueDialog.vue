<script async setup lang="ts">
  import { ref, computed, watch } from 'vue';
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{
    deck: Deck;
    visible: boolean;
  }>();

  const emit = defineEmits<{
    (e: 'update:visible', value: boolean): void;
  }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();

  // Issue types and helper text
  export type IssueType = 'CharacterCount' | 'JapaneseTitle' | 'RomajiTitle' | 'EnglishTitle' | 'Cover' | 'Links' | 'Description' | 'Difficulty' | 'Other';

  const options: { label: string; value: IssueType }[] = [
    { label: 'Character count', value: 'CharacterCount' },
    { label: 'Japanese title', value: 'JapaneseTitle' },
    { label: 'Romaji title', value: 'RomajiTitle' },
    { label: 'English title', value: 'EnglishTitle' },
    { label: 'Cover', value: 'Cover' },
    { label: 'Links', value: 'Links' },
    { label: 'Description', value: 'Description' },
    { label: 'Difficulty', value: 'Difficulty' },
    { label: 'Other', value: 'Other' },
  ];

  const helperByType: Record<IssueType, string> = {
    CharacterCount: 'Report only significant discrepancies in character counts (greater than 10%). Include a source if available.',
    JapaneseTitle: 'Provide the correct Japanese title, with a source if possible.',
    RomajiTitle: 'Provide the correct romanization, supported by a source if possible.',
    EnglishTitle: 'Provide the official English title with a reliable source. Fan translations are not accepted.',
    Cover: 'Specify what is incorrect with the cover (e.g., stretched, broken, or inappropriate/NSFW).',
    Links: 'Indicate which link is missing or broken.',
    Description:
      'Do not report missing descriptions or those written in Japanese. Instead, explain the issue with the existing description (e.g., incorrect, misleading, or NSFW).',
    Difficulty: 'Indicate whether the difficulty should be adjusted (higher or lower). Include a source if possible.',
    Other: 'Provide detailed information about the issue, including relevant links if applicable.',
  };

  const localVisible = ref<boolean>(props.visible);
  watch(
    () => props.visible,
    (v) => (localVisible.value = v)
  );
  watch(localVisible, (v) => emit('update:visible', v));

  const selectedType = ref<IssueType | null>(null);
  const comment = ref<string>('');

  const helperText = computed(() => (selectedType.value ? helperByType[selectedType.value] : 'Please select an option to see details.'));

  const canSubmit = computed(() => !!selectedType.value && comment.value.trim().length > 0);

  const submit = async () => {
    if (!canSubmit.value) return;
    const url = 'media-deck/report';
    const response = await $api<File>(url, {
      method: 'POST',
      body: {
        deckId: props.deck.deckId,
        issueType: selectedType.value,
        comment: comment.value.trim(),
      },
    });

    toast.add({
      severity: 'success',
      summary: 'Success',
      detail: 'Issue reported successfully',
      life: 3000,
    });
    localVisible.value = false;
  };
</script>

<template>
  <Dialog v-model:visible="localVisible" modal header="Report an issue" :style="{ width: '36rem', maxWidth: '90vw' }">
    <div>
      You're reporting an issue with the deck
      <b>{{ localiseTitle(deck) }}</b>
    </div>
    <div class="flex flex-col gap-4">
      <div>
        <div class="text-sm text-gray-500 dark:text-gray-400 mb-2">Select an option</div>
        <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
          <label
            v-for="opt in options"
            :key="opt.value"
            class="flex items-center gap-2 rounded border p-2 cursor-pointer hover:bg-surface-50 hover:dark:bg-surface-800"
          >
            <RadioButton v-model="selectedType" :input-id="'issue-' + opt.value" :value="opt.value" />
            <span :for="'issue-' + opt.value">{{ opt.label }}</span>
          </label>
        </div>
      </div>

      <div class="flex flex-col gap-2">
        <div class="text-sm font-semibold">Description</div>
        <div class="text-sm text-gray-600 dark:text-gray-400">{{ helperText }}</div>
      </div>

      <div class="flex flex-col gap-2">
        <div class="flex items-center justify-between">
          <label for="issue-comment" class="text-sm text-gray-500 dark:text-gray-400">Comment (required)</label>
          <span class="text-xs text-gray-400">{{ comment.length }}/800</span>
        </div>
        <Textarea
          id="issue-comment"
          v-model="comment"
          :auto-resize="true"
          :maxlength="800"
          rows="4"
          placeholder="Provide details, sources, and any useful links..."
        />
        <small v-if="comment.trim().length === 0" class="text-xs text-red-500">A comment is required.</small>
      </div>

      <small v-if="comment.trim().length === 0" class="text-xs">Your User ID will be associated to the report to prevent abuse.</small>

      <div class="flex justify-end gap-2 pt-2">
        <Button label="Cancel" severity="secondary" @click="localVisible = false" />
        <Button label="Submit" icon="pi pi-send" :disabled="!canSubmit" @click="submit" />
      </div>
    </div>
  </Dialog>
</template>

<style scoped></style>
