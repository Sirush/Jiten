<script setup lang="ts">
  import type { ProfileSectionKey } from '~/types';

  defineProps<{ section?: ProfileSectionKey }>();

  const { header, canSee } = useProfileContext();
  await useProfileHeaderReady();
</script>

<template>
  <template v-if="header">
    <ProfileLockedSection v-if="section && !canSee(section)" :section="section" />
    <slot v-else />
  </template>
</template>
