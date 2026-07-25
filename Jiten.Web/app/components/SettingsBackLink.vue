<script setup lang="ts">
  const props = withDefaults(defineProps<{ to?: string; label?: string }>(), { to: '/settings', label: 'Back to settings' });

  const router = useRouter();
  const show = ref(false);

  onMounted(() => {
    const back = (router.options.history.state as { back?: string | null }).back;
    show.value = typeof back === 'string' && back.split(/[?#]/)[0]!.replace(/\/+$/, '') === props.to;
  });
</script>

<template>
  <Button v-if="show" as="router-link" :to="to" :aria-label="label" icon="pi pi-arrow-left" severity="secondary" text rounded />
</template>
