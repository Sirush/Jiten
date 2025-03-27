<script setup lang="ts">
  import InputText from 'primevue/inputtext';

  const router = useRouter();
  const route = useRoute();

  const searchContent = ref<string>(Array.isArray(route.query.text) ? route.query.text[0] || '' : route.query.text || '');

  const onSearch = async () => {
    await navigateTo({
      path: '/parse',
      query: {
        text: searchContent.value,
      },
    });
  };
</script>

<template>
  <div class="flex flex-row">
    <InputText
      v-model="searchContent"
      type="text"
      placeholder="Search a word or a sentence"
      class="w-full"
      maxlength="200"
      @keyup.enter="onSearch"
    />
    <Button label="Search" icon="pi pi-search" class="ml-2" @click="onSearch">
      <Icon name="material-symbols:search-rounded" />
    </Button>
  </div>
</template>

<style scoped></style>
