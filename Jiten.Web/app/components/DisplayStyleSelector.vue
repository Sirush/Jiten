<script setup lang="ts">
  import { DisplayStyle } from '~/types';
  import { useDisplayStyleStore } from '~/stores/displayStyleStore';

  // Bound to a local choice when a v-model is given (subdeck lists); otherwise to the site-wide store.
  const model = defineModel<DisplayStyle>();
  const displayStyleStore = useDisplayStyleStore();
  const displayStyle = computed(() => model.value ?? displayStyleStore.displayStyle);

  const styles = [
    { value: DisplayStyle.Card, label: 'Card View', icon: 'material-symbols:view-agenda-outline' },
    { value: DisplayStyle.Compact, label: 'Compact View', icon: 'material-symbols:grid-view' },
    { value: DisplayStyle.Table, label: 'Table View', icon: 'material-symbols:table-rows' },
  ];

  const currentStyle = computed(() => styles.find((style) => style.value === displayStyle.value) ?? styles[0]);

  const popover = ref();

  const setDisplayStyle = (style: DisplayStyle) => {
    if (model.value !== undefined) model.value = style;
    else displayStyleStore.displayStyle = style;
  };

  const pickDisplayStyle = (style: DisplayStyle) => {
    setDisplayStyle(style);
    popover.value?.hide();
  };
</script>

<template>
  <div class="hidden md:flex gap-2">
    <Tooltip v-for="style in styles" :key="style.value" :content="style.label">
      <Button :class="{ 'p-button-outlined': displayStyle !== style.value }" :aria-label="style.label" @click="setDisplayStyle(style.value)">
        <Icon :name="style.icon" size="1.25em" />
      </Button>
    </Tooltip>
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
