<script setup lang="ts">
  import { type Deck, DeckDownloadType, DeckFormat, DeckOrder } from '~/types';
  import { SelectButton } from 'primevue';
  import { debounce } from 'perfect-debounce';
  import { useApiFetch, useApiFetchPaginated } from '~/composables/useApiFetch';

  const props = defineProps<{
    deck: Deck;
    visible: boolean;
  }>();

  const emit = defineEmits(['update:visible']);

  const localVisible = ref(props.visible);
  const downloading = ref(false);

  const deckOrders = getEnumOptions(DeckOrder, getDeckOrderText);
  const downloadTypes = getEnumOptions(DeckDownloadType, getDownloadTypeText);
  const deckFormats = getEnumOptions(DeckFormat, getDeckFormatText);

  const format = defineModel<DeckFormat>('deckFormat', { default: DeckFormat.Anki });
  const downloadType = defineModel<DeckDownloadType>('downloadType', { default: DeckDownloadType.TopDeckFrequency });
  const deckOrder = defineModel<DeckOrder>('deckOrder', { default: DeckOrder.DeckFrequency });
  const frequencyRange = defineModel<number[]>('frequencyRange');

  onMounted(() => {
    if (!frequencyRange.value) {
      frequencyRange.value = [0, Math.min(props.deck.uniqueWordCount, 5000)];
    }
  });

  watch(
    () => props.visible,
    (newVal) => {
      localVisible.value = newVal;
    }
  );

  watch(
    () => downloadType.value,
    (newVal) => {
      if (newVal == DeckDownloadType.Full) {
        frequencyRange.value = [0, Math.min(props.deck.uniqueWordCount, 5000)];
        currentSliderMax.value = props.deck.uniqueWordCount;
      } else if (newVal == DeckDownloadType.TopDeckFrequency || newVal == DeckDownloadType.TopChronological) {
        frequencyRange.value = [0, Math.min(props.deck.uniqueWordCount, 5000)];
        currentSliderMax.value = props.deck.uniqueWordCount;
      } else if (newVal == DeckDownloadType.TopGlobalFrequency) {
        frequencyRange.value = [1, Math.min(200000, 30000)];
        currentSliderMax.value = 200000;
        updateDebounced();
      }
    }
  );

  watch(
    () => frequencyRange.value,
    () => {
      if (downloadType.value == DeckDownloadType.TopGlobalFrequency) {
        updateDebounced();
      }
    }
  );

  watch(localVisible, (newVal) => {
    emit('update:visible', newVal);
  });

  const updateDebounced = debounce(async () => {
    const { data: response } = await useApiFetch<number>(`media-deck/${props.deck.deckId}/vocabulary-count-frequency`, {
      query: {
        minFrequency: frequencyRange.value![0],
        maxFrequency: frequencyRange.value![1],
      },
    });
    debouncedCurrentCardAmount.value = response;
  }, 500);

  const debouncedCurrentCardAmount = ref(0);

  const url = `media-deck/${props.deck.deckId}/download`;
  const { $api } = useNuxtApp();

  const currentSliderMax = ref(props.deck.uniqueWordCount);

  const currentCardAmount = computed(() => {
    if (downloadType.value == DeckDownloadType.Full) {
      return props.deck.uniqueWordCount;
    } else if (
      downloadType.value == DeckDownloadType.TopDeckFrequency ||
      downloadType.value == DeckDownloadType.TopChronological
    ) {
      return frequencyRange.value![1] - frequencyRange.value![0];
    } else if (downloadType.value == DeckDownloadType.TopGlobalFrequency) {
      return debouncedCurrentCardAmount;
    }

    return 0;
  });

  const downloadFile = async () => {
    try {
      downloading.value = true;
      localVisible.value = false;
      const response = await $api<File>(url, {
        query: {
          format: format.value,
          downloadType: downloadType.value,
          order: deckOrder.value,
          minFrequency: frequencyRange.value![0],
          maxFrequency: frequencyRange.value![1],
        },
      });
      if (response) {
        const blobUrl = window.URL.createObjectURL(new Blob([response], { type: response.type }));
        const link = document.createElement('a');
        link.href = blobUrl;
        if (format.value === DeckFormat.Anki) {
          link.setAttribute('download', `${localiseTitle(props.deck).substring(0, 30)}.apkg`);
        } else if (format.value === DeckFormat.Csv) {
          link.setAttribute('download', `${localiseTitle(props.deck).substring(0, 30)}.csv`);
        } else if (format.value === DeckFormat.Txt) {
          link.setAttribute('download', `${localiseTitle(props.deck).substring(0, 30)}.txt`);
        }

        document.body.appendChild(link);
        link.click();
        link.remove();
        downloading.value = false;
      } else {
        downloading.value = false;
        console.error('Error downloading file.');
      }
    } catch (err) {
      downloading.value = false;
      console.error('Error:', err);
    }
  };

  const updateMinFrequency = (value: number) => {
    if (frequencyRange.value) {
      frequencyRange.value = [value, frequencyRange.value[1]];
    }
  };

  const updateMaxFrequency = (value: number) => {
    if (frequencyRange.value) {
      frequencyRange.value = [frequencyRange.value[0], value];
    }
  };
</script>

<template>
  <Dialog
    v-model:visible="localVisible"
    modal
    :header="`Download deck ${localiseTitle(deck)}`"
    :style="{ width: '30rem' }"
  >
    <div class="flex flex-col gap-2">
      <div>
        <div class="text-gray-500 text-sm">Format</div>
        <SelectButton v-model="format" :options="deckFormats" option-value="value" option-label="label" size="small" />
      </div>
      <span v-if="format == DeckFormat.Anki" class="text-sm">
        Uses the Lapis template from <a href="https://github.com/donkuri/lapis/tree/main">Lapis</a>
      </span>
      <span v-if="format == DeckFormat.Txt" class="text-sm">
        Plain text format, one word per line, vocabulary only. <br >
        Perfect for importing in other websites.
      </span>
      <div>
        <div class="text-gray-500 text-sm">Filter type</div>
        <Select
          v-model="downloadType"
          :options="downloadTypes"
          option-value="value"
          option-label="label"
          size="small"
        />
      </div>
      <div>
        <div class="text-gray-500 text-sm">Sort order</div>
        <Select v-model="deckOrder" :options="deckOrders" option-value="value" option-label="label" size="small" />
      </div>
      <div v-if="downloadType != DeckDownloadType.Full">
        <div class="text-gray-500 text-sm">Range</div>
        <div class="flex flex-row flex-wrap gap-2 items-center">
          <InputNumber
            :model-value="frequencyRange?.[0] ?? 0"
            show-buttons
            fluid
            size="small"
            class="max-w-20 flex-shrink-0"
            @update:model-value="updateMinFrequency"
          />
          <Slider
            v-model="frequencyRange"
            range
            :min="0"
            :max="currentSliderMax"
            class="flex-grow mx-2 flex-basis-auto"
          />
          <InputNumber
            :model-value="frequencyRange?.[1] ?? 0"
            show-buttons
            fluid
            size="small"
            class="max-w-20 flex-shrink-0"
            @update:model-value="updateMaxFrequency"
          />
        </div>
      </div>
      <div>
        This will download <span class="font-bold">{{ currentCardAmount }} cards</span>.
      </div>
      <div class="flex justify-center">
        <Button type="button" label="Download" @click="downloadFile()" />
      </div>
    </div>
  </Dialog>

  <div v-if="downloading" class="!fixed top-1/3 left-1/3 text-center" style="z-index: 9999">
    <div class="text-white font-bold text-lg">Preparing your deck, please wait a few seconds…</div>
    <ProgressSpinner
      style="width: 50px; height: 50px"
      stroke-width="8"
      fill="transparent"
      animation-duration=".5s"
      aria-label="Creating your deck"
    />
  </div>
  <BlockUI :blocked="downloading" full-screen />
</template>

<style scoped></style>
