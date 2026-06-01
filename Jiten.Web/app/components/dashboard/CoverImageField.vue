<script setup lang="ts">
  import { ref, computed, watch, onBeforeUnmount } from 'vue';
  import Button from 'primevue/button';
  import FileUpload from 'primevue/fileupload';
  import CoverTools from '~/components/dashboard/CoverTools.vue';

  defineProps<{
    title: string;
    subtitle?: string;
  }>();

  // The parent keeps these refs because submit reads them; this field just drives them.
  const file = defineModel<File | null>('file', { default: null });
  const url = defineModel<string | null>('url', { default: null });

  const objectUrl = ref<string | null>(null);

  watch(
    file,
    (f) => {
      if (objectUrl.value) {
        URL.revokeObjectURL(objectUrl.value);
        objectUrl.value = null;
      }
      if (f && typeof window !== 'undefined') {
        objectUrl.value = URL.createObjectURL(f);
      }
    },
    { immediate: true }
  );

  onBeforeUnmount(() => {
    if (objectUrl.value) URL.revokeObjectURL(objectUrl.value);
  });

  const previewSrc = computed(() => url.value || objectUrl.value);
  const source = computed<File | string | null>(() => file.value ?? url.value);

  function onSelect(event: { files: File[] }) {
    if (event.files?.length) {
      file.value = event.files[0];
      url.value = null;
    }
  }

  function clear() {
    file.value = null;
    url.value = null;
  }

  function onGenerated(f: File) {
    file.value = f;
    url.value = null;
  }
</script>

<template>
  <div>
    <label class="mb-1 block text-sm font-medium">Cover Image</label>

    <div v-if="previewSrc" class="mb-2 flex items-center">
      <img :src="previewSrc" alt="Cover Preview" class="mr-2 h-48 w-auto border object-contain" />
      <Button class="p-button-text p-button-sm" @click="clear">
        <Icon name="material-symbols-light:close" class="w-full" size="1.5em" />
      </Button>
    </div>

    <FileUpload
      mode="advanced"
      accept="image/*"
      :auto="true"
      choose-label="Select Cover Image"
      :multiple="false"
      class="cover-image-upload w-full"
      :custom-upload="true"
      :show-upload-button="false"
      :show-cancel-button="false"
      drag-drop-text="Select Cover Image or Drag and Drop Here"
      @select="onSelect"
    />

    <CoverTools :source="source" :title="title" :subtitle="subtitle" @update:cover="onGenerated" />
  </div>
</template>
