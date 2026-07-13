<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';

  useHead({
    title: 'Dashboard - Jiten',
  });

  definePageMeta({
    middleware: ['auth-admin'],
  });
</script>

<template>
  <div class="container mx-auto p-4">
    <h1 class="text-3xl font-bold mb-6">Dashboard</h1>

    <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
      <Card class="shadow-md">
        <template #title>Media Management</template>
        <template #content>
          <p class="mb-4">Upload new media</p>
          <div class="flex justify-center">
            <Button label="Add Media" class="p-button-primary" @click="navigateTo('/dashboard/add-media')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Issues</template>
        <template #content>
          <p class="mb-4">Missing titles, missing links, etc</p>
          <div class="flex justify-center">
            <Button label="View Issues" class="p-button-primary" @click="navigateTo('/dashboard/issues')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Data management</template>
        <template #content>
          <p class="mb-4">Reparse media, recompute frequencies & difficulty</p>
          <div class="flex justify-center">
            <Button label="Manage" class="p-button-primary" @click="navigateTo('/dashboard/manage')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Add from Jimaku</template>
        <template #content>
          <p class="mb-4">Add new media from Jimaku</p>
          <div class="flex justify-center">
            <Button label="Add Media" class="p-button-primary" @click="navigateTo('/dashboard/add-jimaku')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Add Webnovel</template>
        <template #content>
          <p class="mb-4">Import a syosetu novel and keep it in sync</p>
          <div class="flex justify-center">
            <Button label="Add Webnovel" class="p-button-primary" @click="navigateTo('/dashboard/add-webnovel')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Maintenance Banner</template>
        <template #content>
          <p class="mb-4">Display a site-wide maintenance notice</p>
          <div class="flex justify-center">
            <Button label="Manage Banner" class="p-button-primary" @click="navigateTo('/dashboard/maintenance')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Tags & Mappings</template>
        <template #content>
          <p class="mb-4">Manage tags, genre mappings, and tag mappings for media metadata</p>
          <div class="flex flex-col gap-2">
            <Button label="Manage Tags" icon="pi pi-tag" class="p-button-primary" @click="navigateTo('/dashboard/tags')" />
            <Button label="Genre Mappings" icon="pi pi-map" class="p-button-primary" @click="navigateTo('/dashboard/genre-mappings')" />
            <Button label="Tag Mappings" icon="pi pi-sitemap" class="p-button-primary" @click="navigateTo('/dashboard/tag-mappings')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Furigana Issues</template>
        <template #content>
          <p class="mb-4">Find and fix kanji words missing furigana</p>
          <div class="flex justify-center">
            <Button label="Manage Furigana" class="p-button-primary" @click="navigateTo('/dashboard/furigana-issues')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Word Sets</template>
        <template #content>
          <p class="mb-4">Create, edit, and manage word sets</p>
          <div class="flex justify-center">
            <Button label="Manage Word Sets" class="p-button-primary" @click="navigateTo('/dashboard/word-sets')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Request Activity</template>
        <template #content>
          <p class="mb-4">Monitor request activity and user contributions</p>
          <div class="flex justify-center">
            <Button label="View Activity" class="p-button-primary" @click="navigateTo('/dashboard/request-activity')" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Send Notification</template>
        <template #content>
          <p class="mb-4">Send a notification to a specific user or to everyone</p>
          <div class="flex justify-center">
            <Button label="Send Notification" class="p-button-primary" @click="navigateTo('/dashboard/send-notification')" />
          </div>
        </template>
      </Card>
    </div>
  </div>
</template>

<style scoped></style>
