<script setup lang="ts">
  import SettingsSrsStudy from '~/components/SettingsSrsStudy.vue';
  import SettingsFsrsParameters from '~/components/SettingsFsrsParameters.vue';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'SRS Settings' });

  const router = useRouter();
  const backLink = computed(() => {
    const prev = router.options.history.state?.back as string | undefined;
    return prev?.startsWith('/srs') ? '/srs/decks' : '/settings';
  });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 flex flex-col gap-4">
    <div class="flex items-center gap-2">
      <NuxtLink :to="backLink">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded />
      </NuxtLink>
      <h1 class="text-2xl font-bold">SRS Settings</h1>
    </div>
    <SettingsSrsStudy />
    <SettingsFsrsParameters />
  </div>
</template>
