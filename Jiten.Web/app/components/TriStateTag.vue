<script setup lang="ts">
  import { computed } from 'vue';
  import Tag from 'primevue/tag';

  export type TagState = 'neutral' | 'include' | 'exclude';

  const props = defineProps<{
    label: string;
    state: TagState;
  }>();

  const emit = defineEmits<{
    'update:state': [state: TagState];
  }>();

  const handleClick = () => {
    const nextState: TagState = props.state === 'neutral' ? 'include' : props.state === 'include' ? 'exclude' : 'neutral';
    emit('update:state', nextState);
  };

  const severity = computed(() => {
    switch (props.state) {
      case 'include':
        return 'success';
      case 'exclude':
        return 'danger';
      default:
        return 'secondary';
    }
  });

  const icon = computed(() => {
    switch (props.state) {
      case 'include':
        return 'pi pi-check';
      case 'exclude':
        return 'pi pi-times';
      default:
        return null;
    }
  });
</script>

<template>
  <Tag :value="label" :severity="severity" :icon="icon" class="cursor-pointer select-none transition-all hover:opacity-80" @click="handleClick" />
</template>
