<script setup lang="ts">
import { ref } from 'vue';
import InputNumber from 'primevue/inputnumber';
import Button from 'primevue/button';
import Checkbox from 'primevue/checkbox';
import OrderList from 'primevue/orderlist';
import { useToast } from 'primevue/usetoast';
import InputText from 'primevue/inputtext';
import InputGroup from 'primevue/inputgroup';

useHead({
  title: 'Add Media from Jimaku - Jiten',
});

definePageMeta({
  middleware: ['auth'],
});

const toast = useToast();
const startId = ref(9000);
const endId = ref(15000);
const currentId = ref<number | null>(null);
const jimakuData = ref<any>(null);
const allFiles = ref<any[]>([]);
const selectedFileUrls = ref<string[]>([]);
const prefix = ref('');
const suffix = ref('');
const { $api } = useNuxtApp();

const fetchNext = async () => {
  if (currentId.value === null) {
    currentId.value = startId.value;
  } else {
    currentId.value++;
  }

  if (currentId.value > endId.value) {
    toast.add({ severity: 'info', summary: 'Info', detail: 'Finished processing all IDs.', life: 3000 });
    return;
  }

  try {
    const response = await $api(`admin/get-jimaku/${currentId.value}`);
    jimakuData.value = response;
    allFiles.value = (jimakuData.value.files || []).sort((a, b) => a.name.localeCompare(b.name));
    selectedFileUrls.value = []; // Start with empty selection
  } catch (error) {
    console.error(error);
    toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, `Failed to fetch Jimaku data for ID ${currentId.value}.`), life: 3000 });
    await fetchNext(); // Try next id
  }
};

const submit = async () => {
  if (!jimakuData.value || !selectedFileUrls.value.length) {
    toast.add({ severity: 'warn', summary: 'Warning', detail: 'No files selected.', life: 3000 });
    return;
  }

  // Use the current order from allFiles.value and filter for selected ones
  // This preserves the user-defined order from the OrderList
  const selectedUrlsSet = new Set(selectedFileUrls.value);
  const orderedSelectedFiles = allFiles.value.filter(f => selectedUrlsSet.has(f.url));

  const payload = {
    jimakuId: jimakuData.value.entry.id,
    files: orderedSelectedFiles.map(f => ({
      ...f,
      name: `${prefix.value}${f.name}${suffix.value}`
    })),
  };

  try {
    const response = await $api('admin/add-jimaku-deck', { method: 'POST', body: payload });
    toast.add({ severity: 'success', summary: 'Success', detail: `Deck '${response.title}' has been queued for processing.`, life: 3000 });
    await fetchNext();
  } catch (error) {
    console.error(error);
    toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to add media.'), life: 3000 });
  }
};

const selectAll = () => {
  selectedFileUrls.value = allFiles.value.map(f => f.url);
};

const deselectAll = () => {
  selectedFileUrls.value = [];
};

const selectByPrefix = () => {
  if (!prefix.value) return;
  const filesToSelect = allFiles.value.filter(f => f.name.toLowerCase().startsWith(prefix.value.toLowerCase()));
  const currentSelectedUrls = new Set(selectedFileUrls.value);
  const newUrlsToAdd = filesToSelect.map(f => f.url).filter(url => !currentSelectedUrls.has(url));
  if (newUrlsToAdd.length > 0) {
    selectedFileUrls.value.push(...newUrlsToAdd);
  }
};

const deselectByPrefix = () => {
  if (!prefix.value) return;
  const prefixFileUrls = new Set(allFiles.value.filter(f => f.name.toLowerCase().startsWith(prefix.value.toLowerCase())).map(f => f.url));
  selectedFileUrls.value = selectedFileUrls.value.filter(url => !prefixFileUrls.has(url));
};

const selectBySuffix = () => {
  if (!suffix.value) return;
  const filesToSelect = allFiles.value.filter(f => f.name.toLowerCase().endsWith(suffix.value.toLowerCase()));
  const currentSelectedUrls = new Set(selectedFileUrls.value);
  const newUrlsToAdd = filesToSelect.map(f => f.url).filter(url => !currentSelectedUrls.has(url));
  if (newUrlsToAdd.length > 0) {
    selectedFileUrls.value.push(...newUrlsToAdd);
  }
};

const deselectBySuffix = () => {
  if (!suffix.value) return;
  const suffixFileUrls = new Set(allFiles.value.filter(f => f.name.toLowerCase().endsWith(suffix.value.toLowerCase())).map(f => f.url));
  selectedFileUrls.value = selectedFileUrls.value.filter(url => !suffixFileUrls.has(url));
};

// Handle reordering - this ensures the order is maintained properly
const onReorder = (event: any) => {
  // The OrderList component automatically updates allFiles.value
  // No additional logic needed here as the submit function already uses allFiles.value order
};
</script>

<template>
  <div class="container mx-auto p-4">
    <h1 class="text-3xl font-bold mb-6">Add Media from Jimaku</h1>

    <div class="grid grid-cols-1 md:grid-cols-2 gap-6 mb-6">
      <div>
        <label for="startId" class="block mb-2">Start ID</label>
        <InputNumber v-model="startId" input-id="startId" />
      </div>
      <div>
        <label for="endId" class="block mb-2">End ID</label>
        <InputNumber v-model="endId" input-id="endId" />
      </div>
    </div>

    <Button label="Start" class="p-button-primary mb-6" @click="fetchNext" />

    <div v-if="jimakuData">
      <h2 class="text-2xl font-bold mb-4">{{ jimakuData.entry.name }} ({{currentId}})</h2>

      <div class="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
        <InputGroup>
          <InputText v-model="prefix" placeholder="Prefix" />
          <Button icon="pi pi-check" aria-label="Select by prefix" @click="selectByPrefix" />
          <Button icon="pi pi-times" aria-label="Deselect by prefix" @click="deselectByPrefix" />
        </InputGroup>
        <InputGroup>
          <InputText v-model="suffix" placeholder="Suffix" />
          <Button icon="pi pi-check" aria-label="Select by suffix" @click="selectBySuffix" />
          <Button icon="pi pi-times" aria-label="Deselect by suffix" @click="deselectBySuffix" />
        </InputGroup>
      </div>

      <div class="flex gap-2 mb-4">
        <Button label="Select All" class="p-button-secondary" @click="selectAll" />
        <Button label="Deselect All" class="p-button-secondary" @click="deselectAll" />
      </div>

      <OrderList
        v-model="allFiles"
        list-style="height: 25rem"
        data-key="url"
        @reorder="onReorder"
      >
        <template #header>Files (drag to reorder)</template>
        <template #item="slotProps">
          <div class="flex items-center p-2">
            <Checkbox v-model="selectedFileUrls" :value="slotProps.item.url" />
            <div class="ml-2">{{ slotProps.item.name }}</div>
          </div>
        </template>
      </OrderList>

      <div class="flex gap-2 mt-6">
        <Button label="Submit" class="p-button-success" @click="submit" />
        <Button label="Skip" class="p-button-warning" @click="fetchNext" />
      </div>
    </div>
  </div>
</template>

<style scoped></style>
