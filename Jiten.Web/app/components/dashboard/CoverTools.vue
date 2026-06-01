<script setup lang="ts">
  import { ref, computed, watch } from 'vue';
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import SelectButton from 'primevue/selectbutton';
  import Slider from 'primevue/slider';
  import Textarea from 'primevue/textarea';
  import Checkbox from 'primevue/checkbox';
  import ProgressSpinner from 'primevue/progressspinner';
  import { useToast } from 'primevue/usetoast';
  import {
    loadImage,
    extractPalette,
    ensureTitleFont,
    renderCover,
    rotateImage,
    canvasToFile,
    shuffleOptions,
    defaultCoverOptions,
    type CoverOptions,
    type CoverPalette,
    type BackgroundStyle,
    type Orientation,
    type HAlign,
    type VAlign,
  } from '~/utils/coverImage';

  const props = defineProps<{
    source: File | string | null;
    title: string;
    subtitle?: string;
  }>();

  const emit = defineEmits<{
    'update:cover': [file: File];
  }>();

  const toast = useToast();
  const { $api } = useNuxtApp();

  const rotating = ref(false);
  const generatorVisible = ref(false);
  const loadingSource = ref(false);
  const previewUrl = ref<string | null>(null);

  const options = ref<CoverOptions | null>(null);
  const palette = ref<CoverPalette | null>(null);
  const swatchTarget = ref<'bg1' | 'bg2' | 'text'>('bg1');

  let sourceImage: HTMLImageElement | null = null;
  let currentCanvas: HTMLCanvasElement | null = null;
  let rafId: number | null = null;

  const styleOptions: { label: string; value: BackgroundStyle }[] = [
    { label: 'Gradient', value: 'gradient' },
    { label: 'Solid', value: 'solid' },
    { label: 'Blurred art', value: 'blurred' },
  ];
  const orientationOptions: { label: string; value: Orientation }[] = [
    { label: 'Horizontal', value: 'horizontal' },
    { label: 'Vertical (tategaki)', value: 'vertical' },
  ];
  const hAlignOptions: { label: string; value: HAlign }[] = [
    { label: 'Left', value: 'left' },
    { label: 'Center', value: 'center' },
    { label: 'Right', value: 'right' },
  ];
  const vAlignOptions: { label: string; value: VAlign }[] = [
    { label: 'Top', value: 'top' },
    { label: 'Middle', value: 'center' },
    { label: 'Bottom', value: 'bottom' },
  ];

  const targetOptions = computed(() => {
    const style = options.value?.style;
    if (style === 'solid')
      return [
        { label: 'Background', value: 'bg1' as const },
        { label: 'Text', value: 'text' as const },
      ];
    if (style === 'blurred') return [{ label: 'Text', value: 'text' as const }];
    return [
      { label: 'Background 1', value: 'bg1' as const },
      { label: 'Background 2', value: 'bg2' as const },
      { label: 'Text', value: 'text' as const },
    ];
  });

  function showToast(severity: 'success' | 'info' | 'warn' | 'error', summary: string, detail = '') {
    toast.add({ severity, summary, detail, life: 3000 });
  }

  // Resolve the current cover (uploaded File or remote URL) into a Blob. Remote URLs go
  // through the admin proxy so the canvas isn't tainted and we can read pixels.
  async function resolveBlob(source: File | string): Promise<Blob> {
    if (source instanceof File) return source;
    return await $api<Blob>('admin/proxy-image', { query: { url: source }, responseType: 'blob' });
  }

  async function rotate(degrees: 90 | 270) {
    if (!props.source || rotating.value) return;
    rotating.value = true;
    try {
      const blob = await resolveBlob(props.source);
      const img = await loadImage(blob);
      const file = await canvasToFile(rotateImage(img, degrees), 'cover.jpg');
      emit('update:cover', file);
    } catch (error) {
      console.error('Cover rotation failed:', error);
      showToast('error', 'Rotation failed', 'Could not rotate the cover image.');
    } finally {
      rotating.value = false;
    }
  }

  function renderPreview() {
    if (!options.value) return;
    currentCanvas = renderCover(options.value, sourceImage ?? undefined);
    previewUrl.value = currentCanvas.toDataURL('image/jpeg', 0.92);
  }

  function scheduleRender() {
    if (rafId !== null) return;
    rafId = requestAnimationFrame(() => {
      rafId = null;
      renderPreview();
    });
  }

  async function openGenerator() {
    if (!props.source) return;
    generatorVisible.value = true;
    loadingSource.value = true;
    previewUrl.value = null;
    sourceImage = null;
    currentCanvas = null;
    try {
      const blob = await resolveBlob(props.source);
      sourceImage = await loadImage(blob);
      palette.value = extractPalette(sourceImage);
      await ensureTitleFont();
      options.value = defaultCoverOptions(palette.value, props.title, props.subtitle ?? '');
      swatchTarget.value = 'bg1';
      renderPreview();
    } catch (error) {
      console.error('Cover generation failed:', error);
      showToast('error', 'Generation failed', 'Could not load the source image.');
      generatorVisible.value = false;
    } finally {
      loadingSource.value = false;
    }
  }

  function applySwatch(color: string) {
    if (!options.value) return;
    if (swatchTarget.value === 'bg1') options.value.bgColor1 = color;
    else if (swatchTarget.value === 'bg2') options.value.bgColor2 = color;
    else options.value.textColor = color;
  }

  function shuffle() {
    if (!options.value || !palette.value) return;
    Object.assign(options.value, shuffleOptions(options.value, palette.value));
  }

  async function useGeneratedCover() {
    if (!currentCanvas) return;
    try {
      const file = await canvasToFile(currentCanvas, 'cover.jpg');
      emit('update:cover', file);
      generatorVisible.value = false;
    } catch (error) {
      console.error('Cover encoding failed:', error);
      showToast('error', 'Generation failed', 'Could not encode the generated cover.');
    }
  }

  watch(options, scheduleRender, { deep: true });

  // Keep the active swatch target valid when the style changes.
  watch(
    () => options.value?.style,
    () => {
      if (!targetOptions.value.some((t) => t.value === swatchTarget.value)) {
        swatchTarget.value = targetOptions.value[0].value;
      }
    }
  );
</script>

<template>
  <div class="mt-2 flex flex-wrap items-center gap-2">
    <Button type="button" size="small" severity="secondary" :disabled="!source || rotating" :loading="rotating" @click="rotate(270)">
      <Icon name="material-symbols-light:rotate-left" size="1.3em" />
      <span class="ml-1">Rotate left</span>
    </Button>
    <Button type="button" size="small" severity="secondary" :disabled="!source || rotating" :loading="rotating" @click="rotate(90)">
      <Icon name="material-symbols-light:rotate-right" size="1.3em" />
      <span class="ml-1">Rotate right</span>
    </Button>
    <Button type="button" size="small" :disabled="!source" @click="openGenerator">
      <Icon name="material-symbols-light:auto-awesome" size="1.3em" />
      <span class="ml-1">Generate cover…</span>
    </Button>

    <Dialog v-model:visible="generatorVisible" modal header="Generate Cover" :style="{ width: '820px' }" :breakpoints="{ '900px': '95vw' }">
      <div v-if="loadingSource" class="flex h-[480px] items-center justify-center">
        <ProgressSpinner style="width: 48px; height: 48px" />
      </div>

      <div v-else-if="options" class="flex flex-col gap-4 md:flex-row">
        <!-- Preview -->
        <div class="flex shrink-0 justify-center">
          <div class="flex h-[480px] w-[320px] items-center justify-center rounded border bg-gray-50 dark:bg-gray-800">
            <img v-if="previewUrl" :src="previewUrl" alt="Generated cover preview" class="h-full w-auto rounded" />
          </div>
        </div>

        <!-- Controls -->
        <div class="flex max-h-[480px] flex-1 flex-col gap-3 overflow-y-auto pr-1">
          <div>
            <label class="mb-1 block text-sm font-medium">Title</label>
            <Textarea v-model="options.title" class="w-full" :rows="2" auto-resize />
          </div>
          <div>
            <label class="mb-1 block text-sm font-medium">Subtitle</label>
            <Textarea v-model="options.subtitle" class="w-full" :rows="1" auto-resize placeholder="Optional (e.g. romaji)" />
          </div>

          <div>
            <label class="mb-1 block text-sm font-medium">Background</label>
            <SelectButton v-model="options.style" :options="styleOptions" option-label="label" option-value="value" :allow-empty="false" />
          </div>

          <!-- Colors -->
          <div v-if="palette && options.style !== 'blurred'">
            <label class="mb-1 block text-sm font-medium">Colors</label>
            <SelectButton v-model="swatchTarget" :options="targetOptions" option-label="label" option-value="value" :allow-empty="false" class="mb-2" />
            <div class="mb-2 flex flex-wrap gap-1">
              <button
                v-for="c in palette.swatches"
                :key="c"
                type="button"
                class="h-7 w-7 rounded border border-gray-300"
                :style="{ backgroundColor: c }"
                @click="applySwatch(c)"
              />
            </div>
            <div class="flex flex-wrap items-center gap-3 text-sm">
              <label class="flex items-center gap-1">BG 1 <input v-model="options.bgColor1" type="color" /></label>
              <label v-if="options.style === 'gradient'" class="flex items-center gap-1">BG 2 <input v-model="options.bgColor2" type="color" /></label>
              <label class="flex items-center gap-1">Text <input v-model="options.textColor" type="color" /></label>
            </div>
          </div>
          <div v-else class="flex items-center gap-1 text-sm">
            <label class="flex items-center gap-1">Text color <input v-model="options.textColor" type="color" /></label>
          </div>

          <!-- Gradient angle -->
          <div v-if="options.style === 'gradient'">
            <label class="mb-1 block text-sm font-medium">Gradient angle — {{ options.gradientAngle }}°</label>
            <Slider v-model="options.gradientAngle" :min="0" :max="360" />
          </div>

          <!-- Blurred controls -->
          <template v-if="options.style === 'blurred'">
            <div>
              <label class="mb-1 block text-sm font-medium">Blur — {{ options.blurAmount }}px</label>
              <Slider v-model="options.blurAmount" :min="0" :max="40" />
            </div>
            <div>
              <label class="mb-1 block text-sm font-medium">Darkness — {{ Math.round(options.scrimDarkness * 100) }}%</label>
              <Slider v-model="options.scrimDarkness" :min="0" :max="1" :step="0.05" />
            </div>
          </template>

          <div v-if="options.style !== 'blurred'" class="flex items-center gap-2">
            <Checkbox v-model="options.showDividers" :binary="true" input-id="dividers" />
            <label for="dividers" class="text-sm">Divider lines</label>
          </div>

          <!-- Text layout -->
          <div>
            <label class="mb-1 block text-sm font-medium">Orientation</label>
            <SelectButton v-model="options.orientation" :options="orientationOptions" option-label="label" option-value="value" :allow-empty="false" />
          </div>
          <div class="flex flex-wrap gap-4">
            <div>
              <label class="mb-1 block text-sm font-medium">Align</label>
              <SelectButton v-model="options.hAlign" :options="hAlignOptions" option-label="label" option-value="value" :allow-empty="false" />
            </div>
            <div>
              <label class="mb-1 block text-sm font-medium">Position</label>
              <SelectButton v-model="options.vAlign" :options="vAlignOptions" option-label="label" option-value="value" :allow-empty="false" />
            </div>
          </div>

          <div class="flex items-center gap-2">
            <Checkbox v-model="options.autoFontSize" :binary="true" input-id="autofit" />
            <label for="autofit" class="text-sm">Auto font size</label>
          </div>
          <div v-if="!options.autoFontSize">
            <label class="mb-1 block text-sm font-medium">Font size — {{ options.fontSize }}px</label>
            <Slider v-model="options.fontSize" :min="16" :max="96" />
          </div>
          <div>
            <label class="mb-1 block text-sm font-medium">Font weight — {{ options.fontWeight }}</label>
            <Slider v-model="options.fontWeight" :min="400" :max="900" :step="100" />
          </div>

          <div>
            <Button type="button" size="small" severity="secondary" outlined @click="shuffle">
              <Icon name="material-symbols-light:shuffle" size="1.3em" />
              <span class="ml-1">Shuffle</span>
            </Button>
          </div>
        </div>
      </div>

      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="generatorVisible = false" />
        <Button label="Use this cover" :disabled="!previewUrl || loadingSource" @click="useGeneratedCover" />
      </template>
    </Dialog>
  </div>
</template>
