<script setup lang="ts">
  import type { KanjiComponent, KanjiUsedIn } from '~/types';

  const props = defineProps<{
    character: string;
    components: KanjiComponent[];
    usedIn: KanjiUsedIn[];
    usedInTotal: number;
  }>();

  const { $api } = useNuxtApp();
  const NuxtLink = resolveComponent('NuxtLink');

  const allUsedIn = ref<KanjiUsedIn[] | null>(null);
  const loadingUsedIn = ref(false);
  const visibleUsedIn = computed(() => allUsedIn.value ?? props.usedIn);

  const loadAllUsedIn = async () => {
    loadingUsedIn.value = true;
    try {
      allUsedIn.value = await $api<KanjiUsedIn[]>(`kanji/${encodeURIComponent(props.character)}/used-in`);
    } finally {
      loadingUsedIn.value = false;
    }
  };

  const roleLabel = (component: KanjiComponent) => [component.isRadical ? 'radical' : null, component.isPhonetic ? 'phonetic' : null].filter(Boolean).join(', ');

  const tileTooltip = (component: KanjiComponent) => {
    const parts: string[] = [];
    if (component.original) parts.push(`Variant of ${component.original}`);
    if (component.isPhonetic) parts.push("Phonetic component (音符): often hints at the on'yomi");
    return parts.join('. ');
  };
</script>

<template>
  <div v-if="components.length > 0" class="border-surface-200 dark:border-surface-700 border rounded-lg p-4">
    <h2 class="text-lg font-semibold mb-3">Composed of</h2>
    <div class="flex flex-wrap gap-2">
      <Tooltip v-for="component in components" :key="component.character" :content="tileTooltip(component)">
        <component
          :is="component.linkCharacter ? NuxtLink : 'div'"
          :to="component.linkCharacter ? `/kanji/${component.linkCharacter}` : undefined"
          class="inline-flex items-center gap-2 px-3 py-2 rounded-lg border border-surface-200 dark:border-surface-700"
          :class="component.linkCharacter ? 'hover:border-primary-500 dark:hover:border-primary-400 hover:bg-surface-50 dark:hover:bg-surface-800 transition-colors' : ''"
        >
          <span class="text-2xl font-medium" lang="ja">{{ component.character }}</span>
          <span v-if="component.meaning || roleLabel(component)" class="flex flex-col">
            <span v-if="component.meaning" class="text-sm text-surface-700 dark:text-surface-300 max-w-[10rem] truncate">{{ component.meaning }}</span>
            <span v-if="roleLabel(component)" class="text-[11px] text-primary-600 dark:text-primary-400">{{ roleLabel(component) }}</span>
          </span>
        </component>
      </Tooltip>
    </div>
    <KanjiVgCredit />
  </div>

  <div v-if="usedInTotal > 0" class="border-surface-200 dark:border-surface-700 border rounded-lg p-4">
    <h2 class="text-lg font-semibold mb-3">
      Kanji containing <span lang="ja">{{ character }}</span>
      <span class="ml-1 text-sm font-normal text-surface-500 dark:text-surface-400">({{ usedInTotal }})</span>
    </h2>
    <div class="flex flex-wrap gap-1.5">
      <Tooltip v-for="kanji in visibleUsedIn" :key="kanji.character" :content="kanji.meaning ?? ''">
        <NuxtLink
          :to="`/kanji/${kanji.character}`"
          class="inline-flex items-center justify-center w-11 h-11 rounded-lg border border-surface-200 dark:border-surface-700 text-2xl hover:border-primary-500 dark:hover:border-primary-400 hover:bg-surface-50 dark:hover:bg-surface-800 transition-colors"
          lang="ja"
        >
          {{ kanji.character }}
        </NuxtLink>
      </Tooltip>
    </div>
    <div v-if="usedInTotal > usedIn.length" class="mt-2">
      <button
        v-if="!allUsedIn"
        class="text-sm text-primary-600 dark:text-primary-400 hover:underline cursor-pointer"
        :disabled="loadingUsedIn"
        @click="loadAllUsedIn"
      >
        {{ loadingUsedIn ? 'Loading...' : `View all ${usedInTotal}` }}
      </button>
      <button v-else class="text-sm text-primary-600 dark:text-primary-400 hover:underline cursor-pointer" @click="allUsedIn = null">View less</button>
    </div>
    <KanjiVgCredit v-if="components.length === 0" />
  </div>
</template>
