<script setup lang="ts">
  import { DisplayStyle } from '~/types';
  import { useDisplayStyleStore } from '~/stores/displayStyleStore';

  const displayStyleStore = useDisplayStyleStore();
  const displayStyle = computed(() => displayStyleStore.displayStyle);

  const styles = [
    { value: DisplayStyle.Card, label: 'Card View', icon: 'material-symbols:view-agenda-outline' },
    { value: DisplayStyle.Compact, label: 'Compact View', icon: 'material-symbols:grid-view' },
    { value: DisplayStyle.Table, label: 'Table View', icon: 'material-symbols:table-rows' },
  ];

  const currentStyle = computed(() => styles.find((style) => style.value === displayStyle.value) ?? styles[0]);

  const popover = ref();

  const setDisplayStyle = (style: DisplayStyle) => {
    displayStyleStore.displayStyle = style;
  };

  const pickDisplayStyle = (style: DisplayStyle) => {
    setDisplayStyle(style);
    popover.value?.hide();
  };
</script>

<template>
  <div class="hidden md:flex gap-2">
    <Button
      v-for="style in styles"
      :key="style.value"
      v-tooltip="style.label"
      :class="{ 'p-button-outlined': displayStyle !== style.value }"
      @click="setDisplayStyle(style.value)"
    >
      <Icon :name="style.icon" size="1.25em" />
    </Button>
  </div>

  <!-- Three 44px buttons leave no room for the search field in the single mobile toolbar row.
       The breakpoint sits on the wrapper: PrimeVue's runtime .p-button display rule outranks
       a `hidden` utility placed on the Button itself. -->
  <div class="md:hidden shrink-0">
    <Button class="px-2!" :aria-label="currentStyle.label" @click="popover.toggle($event)">
      <Icon :name="currentStyle.icon" size="1.25em" />
    </Button>
  </div>

  <Popover ref="popover" class="md:hidden">
    <div class="flex flex-col gap-1">
      <Button
        v-for="style in styles"
        :key="style.value"
        :severity="displayStyle === style.value ? 'primary' : 'secondary'"
        :text="displayStyle !== style.value"
        size="small"
        class="justify-start! gap-2"
        @click="pickDisplayStyle(style.value)"
      >
        <Icon :name="style.icon" size="1.25em" />
        {{ style.label }}
      </Button>
    </div>
  </Popover>
</template>

<style scoped></style>
