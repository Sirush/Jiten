<script setup lang="ts">
  defineProps<{
    isSubscribed: boolean;
    compact?: boolean;
  }>();

  defineEmits<{
    toggle: [];
  }>();
</script>

<template>
  <Button
    v-if="compact"
    v-tooltip.top="isSubscribed ? 'Unsubscribe' : 'Subscribe'"
    :icon="isSubscribed ? 'pi pi-bell' : 'pi pi-bell-slash'"
    :severity="isSubscribed ? 'primary' : 'secondary'"
    :outlined="!isSubscribed"
    size="small"
    rounded
    @click="$emit('toggle')"
  />
  <Button
    v-else
    :icon="isSubscribed ? 'pi pi-bell' : 'pi pi-bell-slash'"
    :label="isSubscribed ? 'Subscribed' : 'Subscribe'"
    :severity="isSubscribed ? 'primary' : 'secondary'"
    :outlined="!isSubscribed"
    @click="$emit('toggle')"
  />
</template>
