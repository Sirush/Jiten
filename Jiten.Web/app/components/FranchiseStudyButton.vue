<script setup lang="ts">
  import type { FranchiseNode, MediaGroupRef, MediaType } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { mediaGroupKindWord } from '~/utils/mediaGroup';

  const props = defineProps<{
    group: MediaGroupRef;
    groupName: string;
    members: FranchiseNode[];
    mediaTypes: MediaType[];
    excludedDeckIds: number[];
  }>();

  const route = useRoute();
  const router = useRouter();
  const authStore = useAuthStore();

  const scopeWord = computed(() => mediaGroupKindWord(props.group.kind).toLowerCase());
  const showStudyDialog = ref(false);
  const guestStudyPopover = ref<{ show: (event: Event, target?: HTMLElement) => void } | null>(null);
  const studyRedirect = computed(() => router.resolve({ path: route.path, query: { ...route.query, study: '1' } }).fullPath);
  const preselectedGroup = computed(() => ({
    kind: props.group.kind,
    id: props.group.id,
    name: props.groupName,
    members: props.members,
    mediaTypes: props.mediaTypes,
    excludedDeckIds: props.excludedDeckIds,
  }));

  function onStudy(event: MouseEvent) {
    if (authStore.isAuthenticated) {
      showStudyDialog.value = true;
      return;
    }
    const target = event.currentTarget as HTMLElement;
    guestStudyPopover.value?.show({ currentTarget: target } as unknown as Event, target);
  }

  onMounted(() => {
    if (route.query.study !== '1') return;
    if (authStore.isAuthenticated) showStudyDialog.value = true;
    router.replace({ query: { ...route.query, study: undefined } });
  });
</script>

<template>
  <Button :label="`Study this ${scopeWord}`" icon="pi pi-play" size="small" class="min-h-11 shrink-0 md:pointer-fine:min-h-9" @click="onStudy" />
  <GuestAccountPopover
    v-if="!authStore.isAuthenticated"
    ref="guestStudyPopover"
    :message="`Study the vocabulary of every title in this ${scopeWord} with Jiten's SRS, powered by the FSRS-7 algorithm.`"
    prompt="franchise_study_button"
    :redirect="studyRedirect"
  />
  <LazySrsAddDeckDialog v-if="showStudyDialog" v-model:visible="showStudyDialog" :preselected-group="preselectedGroup" />
</template>
