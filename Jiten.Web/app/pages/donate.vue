<script setup lang="ts">
  useSeoMeta({
    title: 'Support Jiten',
    description: 'Help support Jiten and keep it free for Japanese learners everywhere.',
  });
</script>

<template>
  <div class="container mx-auto px-4 py-10">
    <Card class="max-w-2xl mx-auto shadow-lg">
      <template #title>
        <div class="flex items-center gap-2">
          <Icon name="mdi:heart" class="text-red-500" />
          <span>Support Jiten</span>
        </div>
      </template>

      <template #content>
        <p class="text-gray-700 dark:text-gray-300 leading-relaxed mb-4">
          Jiten is a free and open-source project. Your support helps cover server costs and allows me to dedicate more time to developing new features and
          improving the platform for everyone.
        </p>

        <p class="text-gray-700 dark:text-gray-300 leading-relaxed mb-4">
          Every contribution, no matter how small, makes a real difference and helps keep Jiten running and growing. Thank you for being part of this journey!
        </p>

        <Message severity="info" :closable="false" class="mb-6">
          Donations are purely extra and do not include
          <NuxtLink to="/jiten-plus">Jiten+</NuxtLink>
          . If you want the Jiten+ features, subscribe
          <NuxtLink to="/jiten-plus">there instead</NuxtLink>
          .
        </Message>

        <Divider />

        <div class="flex flex-col items-center gap-3 my-6">
          <a href="https://github.com/sponsors/Sirush" target="_blank" rel="noopener noreferrer">
            <Button class="w-52" severity="secondary" outlined>
              <Icon name="mdi:github" class="mr-3" size="1.25em" />
              GitHub Sponsors
            </Button>
          </a>

          <a href="https://ko-fi.com/jiten" target="_blank" rel="noopener noreferrer">
            <Button class="w-52" severity="info" outlined>
              <Icon name="simple-icons:kofi" class="mr-3" size="1.25em" />
              Ko-fi
            </Button>
          </a>

          <a href="https://patreon.com/JitenMoe" target="_blank" rel="noopener noreferrer">
            <Button class="w-52" severity="help" outlined>
              <Icon name="mdi:patreon" class="mr-3" size="1.25em" />
              Patreon
            </Button>
          </a>
        </div>

        <p class="text-sm text-gray-500 dark:text-gray-400 text-center">
          Can't donate? No worries! You can simply share Jiten with your fellow learners, or why not
          <NuxtLink to="/contribute">contribute</NuxtLink>
          to the media database?
        </p>
      </template>
    </Card>
  </div>
</template>
