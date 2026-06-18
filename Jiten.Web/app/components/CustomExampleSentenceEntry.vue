<script setup lang="ts">
  import type { UserExampleSentenceDto } from '~/types';

  const props = defineProps<{
    sentence: UserExampleSentenceDto;
    // When true, shows an edit pencil that opens the inline editor (used on the vocabulary page).
    editable?: boolean;
  }>();

  const emit = defineEmits<{
    changed: [];
  }>();

  const authStore = useAuthStore();
  const editing = ref(false);

  const formattedText = computed(() => parseCustomSentenceHtml(props.sentence.text));
  const plainText = computed(() => props.sentence.text.replace(/\*\*/g, ''));

  const canEdit = computed(() => props.editable && authStore.isAuthenticated);

  function onChanged() {
    editing.value = false;
    emit('changed');
  }
</script>

<template>
  <div class="flex flex-col">
    <InlineSentenceEditor
      v-if="editing"
      :initial-text="sentence.text"
      :initial-source="sentence.source ?? ''"
      :user-sentence-id="sentence.userExampleSentenceId"
      class="mb-2"
      @saved="onChanged"
      @deleted="onChanged"
      @cancel="editing = false"
    />
    <template v-else>
    <blockquote class="relative inline-block border-l-4 border-yellow-500 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r shadow-sm overflow-hidden">
      <div class="flex items-start gap-2">
        <div class="md:text-lg text-sm flex-1" lang="ja" v-html="formattedText" />
        <TtsButton :text="plainText" :custom-sentence-id="sentence.userExampleSentenceId" type="sentence" size="sm" class="mt-0.5 shrink-0" />
        <button
          v-if="canEdit"
          class="inline-flex items-center justify-center text-surface-400 hover:text-primary-500 transition-colors mt-0.5 shrink-0 cursor-pointer"
          title="Edit sentence"
          @click="editing = true"
        >
          <i class="pi pi-pencil text-sm" />
        </button>
      </div>
    </blockquote>
    <div v-if="sentence.source" class="flex items-center mb-2">
      <span class="text-xs italic mr-2 ml-4">Source:</span>
      <span class="text-xs">{{ sentence.source }}</span>
    </div>
    </template>
  </div>
</template>
