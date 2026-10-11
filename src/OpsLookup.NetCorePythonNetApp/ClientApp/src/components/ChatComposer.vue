<script setup lang="ts">
import { nextTick, ref } from 'vue'
import AppIcon from './AppIcon.vue'

const props = defineProps<{ busy: boolean }>()
const emit = defineEmits<{ send: [message: string] }>()

const text = ref('')
const field = ref<HTMLTextAreaElement | null>(null)

function autosize() {
  const el = field.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 180)}px`
}

function submit() {
  const value = text.value.trim()
  if (!value || props.busy) return
  emit('send', value)
  text.value = ''
  nextTick(autosize)
}

function onKeydown(event: KeyboardEvent) {
  // Enter sends; Shift+Enter is a newline. Guard the modifier check explicitly,
  // since Shift+Enter also reports key === 'Enter'.
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    submit()
  }
}
</script>

<template>
  <form class="mx-auto w-full max-w-3xl" @submit.prevent="submit">
    <div
      class="flex items-end gap-2 rounded-2xl border border-line bg-surface p-2 shadow-sm transition-shadow focus-within:border-accent focus-within:ring-4 focus-within:ring-accent-ring"
      :class="busy ? 'opacity-70' : ''"
    >
      <label class="sr-only" for="composer-input">Describe the ticket</label>
      <textarea
        id="composer-input"
        ref="field"
        v-model="text"
        rows="1"
        placeholder="Describe the ticket in your own words…"
        :disabled="busy"
        class="max-h-[180px] min-h-[2.75rem] flex-1 resize-none border-0 bg-transparent px-2.5 py-2.5 text-[0.9375rem] leading-relaxed text-ink placeholder:text-muted focus:outline-none disabled:cursor-not-allowed"
        @input="autosize"
        @keydown="onKeydown"
      />

      <button
        type="submit"
        :disabled="busy || !text.trim()"
        class="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent text-white shadow-sm transition-colors hover:bg-accent-hover focus-visible:outline-offset-4 disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-accent"
        :aria-label="busy ? 'Waiting for the agent' : 'Send message'"
      >
        <AppIcon v-if="!busy" name="send" class="size-[1.15rem]" />
        <span v-else class="size-4 animate-spin rounded-full border-2 border-white/35 border-t-white" />
      </button>
    </div>

    <p class="mt-2 px-1 text-center text-[0.6875rem] text-muted">
      Enter to send · Shift + Enter for a new line
    </p>
  </form>
</template>