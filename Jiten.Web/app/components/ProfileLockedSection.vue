<script setup lang="ts">
  import type { ProfileSectionKey } from '~/types';

  const props = withDefaults(
    defineProps<{
      section?: ProfileSectionKey;
      icon?: string;
      title?: string;
      message?: string;
      /** Stands in for the whole profile, so it carries the page's h1 and a roomier card. */
      page?: boolean;
    }>(),
    { section: undefined, icon: 'material-symbols:lock', title: undefined, message: undefined, page: false }
  );

  const { handle } = useProfileContext();

  const TITLES: Partial<Record<ProfileSectionKey, string>> = {
    library: 'This library is private',
    vocabulary: 'This vocabulary is private',
    stats: 'These stats are private',
  };

  const heading = computed(() => props.title ?? (props.section && TITLES[props.section]) ?? 'This section is private');
</script>

<template>
  <Card :class="page ? 'mt-8' : 'py-6'">
    <template #content>
      <div class="flex flex-col items-center gap-3 text-center" :class="{ 'py-8': page }">
        <Icon :name="icon" :size="page ? '3.5rem' : '3rem'" class="text-surface-400" />
        <component :is="page ? 'h1' : 'h2'" class="text-xl font-semibold">{{ heading }}</component>
        <p v-if="message" class="text-muted-color">{{ message }}</p>
        <slot>
          <Button as="router-link" :to="profilePath(handle)" label="Back to overview" icon="pi pi-arrow-left" />
        </slot>
      </div>
    </template>
  </Card>
</template>
