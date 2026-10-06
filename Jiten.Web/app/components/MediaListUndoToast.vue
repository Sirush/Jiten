<script setup lang="ts">
import Toast from 'primevue/toast';
import type {ToastMessageOptions} from 'primevue/toast';
import {useToast} from 'primevue/usetoast';
import {UNDO_TOAST_GROUP, type UndoDetail} from '~/composables/useMediaListSync';

const toast = useToast();

function undo(message: ToastMessageOptions) {
  toast.remove(message);
  void (message.detail as UndoDetail).run();
}
</script>

<template>
  <Toast position="bottom-center" :group="UNDO_TOAST_GROUP" :pt="{ root: { class: '!z-[20000]' } }">
    <template #message="{ message }">
      <div class="flex w-full items-center gap-3">
        <span class="text-sm font-medium">{{ message.summary }}</span>
        <Button label="Undo" size="small" text class="ml-auto shrink-0" @click="undo(message)"/>
      </div>
    </template>
  </Toast>
</template>
