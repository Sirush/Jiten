<script setup lang="ts">
  import Popover from 'primevue/popover';
  import SelectButton from 'primevue/selectbutton';
  import { franchiseCardSizeOptions, franchiseDensityOptions, franchiseLinkModeOptions, useFranchiseDisplay } from '~/composables/useFranchiseDisplay';

  const props = defineProps<{
    view: 'timeline' | 'series';
    showLinks: boolean;
  }>();

  const { linkMode, cardSize, density } = useFranchiseDisplay();
  const uid = useId();

  const popover = ref<InstanceType<typeof Popover> | null>(null);
  const open = ref(false);

  watch(
    () => props.view,
    () => nextTick(() => popover.value?.hide())
  );

  const toggleButtonPt = { pcToggleButton: { root: { class: 'min-h-11 flex-auto text-center text-sm leading-tight sm:whitespace-nowrap sm:pointer-fine:min-h-9' } } };
</script>

<template>
  <Button
    severity="secondary"
    text
    size="small"
    class="min-h-11 min-w-11 shrink-0 justify-center gap-1.5 px-2.5 sm:px-3"
    aria-label="Display"
    aria-haspopup="dialog"
    :aria-expanded="open"
    :aria-controls="open ? `${uid}-display` : undefined"
    @click="popover?.toggle($event)"
  >
    <i class="pi pi-cog" aria-hidden="true" />
    <span class="text-sm">Display</span>
  </Button>

  <Popover ref="popover" @show="open = true" @hide="open = false">
    <div :id="`${uid}-display`" role="dialog" aria-label="Display" class="flex w-max min-w-[min(20rem,calc(100vw_-_2rem))] max-w-[calc(100vw_-_2rem)] flex-col gap-4 p-1">
      <template v-if="view === 'timeline'">
        <div v-if="showLinks" class="flex flex-col gap-1.5">
          <span :id="`${uid}-links`" class="text-sm font-semibold">Links</span>
          <SelectButton
            v-model="linkMode"
            :options="franchiseLinkModeOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            :aria-labelledby="`${uid}-links`"
            class="w-full"
            :pt="toggleButtonPt"
          />
        </div>
        <div class="flex flex-col gap-1.5">
          <span :id="`${uid}-cards`" class="text-sm font-semibold">Cards</span>
          <SelectButton
            v-model="cardSize"
            :options="franchiseCardSizeOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            :aria-labelledby="`${uid}-cards`"
            class="w-full"
            :pt="toggleButtonPt"
          />
        </div>
      </template>
      <div v-else class="flex flex-col gap-1.5">
        <span :id="`${uid}-density`" class="text-sm font-semibold">Density</span>
        <SelectButton
          v-model="density"
          :options="franchiseDensityOptions"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-labelledby="`${uid}-density`"
          class="w-full"
          :pt="toggleButtonPt"
        />
      </div>
    </div>
  </Popover>
</template>
