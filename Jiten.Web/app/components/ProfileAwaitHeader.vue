<script setup lang="ts">
  const { header } = useProfileContext();
  await useProfileHeaderReady();

  if (import.meta.server && header.value?.displayName) defineOgImage('ProfileOgImage', profileOgImageProps(header.value));
</script>

<template>
  <slot />
</template>
