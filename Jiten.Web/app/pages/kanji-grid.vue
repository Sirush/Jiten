<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';
  import { ownProfileHandle } from '~/utils/profileAppearance';

  definePageMeta({ middleware: ['auth'], wide: true });
  useHead({ title: 'Kanji grid', htmlAttrs: { class: 'stable-gutter' } });

  const auth = useAuthStore();
  const handle = computed(() => ownProfileHandle(auth.user));
</script>

<template>
  <div class="flex flex-col gap-4 py-2">
    <div>
      <h1 class="text-2xl font-bold">Kanji grid</h1>
      <p class="text-sm text-muted-color">Every kanji, most frequent first, coloured by how well you know the words that use it.</p>
    </div>
    <Card>
      <template #content>
        <KanjiGrid v-if="handle" :username="handle" />
      </template>
    </Card>
  </div>
</template>
