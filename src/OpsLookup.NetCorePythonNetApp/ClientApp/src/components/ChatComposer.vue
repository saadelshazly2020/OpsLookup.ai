<script setup lang="ts">
import { nextTick, ref } from 'vue'

const props = defineProps<{ busy: boolean }>()
const emit = defineEmits<{ send: [message: string] }>()

const text = ref('')
const field = ref<HTMLTextAreaElement | null>(null)

function autosize() {
  const el = field.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 160)}px`
}

function submit() {
  const value = text.value.trim()
  if (!value || props.busy) return
  emit('send', value)
  text.value = ''
  nextTick(autosize)
}

function onKeydown(event: KeyboardEvent) {
  // Enter sends; Shift+Enter is a newline.
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
    submit()
  }
}
</script>

<template>
  <form class="composer" @submit.prevent="submit">
    <label class="sr-only" for="composer-input">Describe the ticket</label>
    <textarea
      id="composer-input"
      ref="field"
      v-model="text"
      rows="1"
      placeholder="Describe the ticket…  (Enter to send, Shift+Enter for a new line)"
      :disabled="busy"
      @input="autosize"
      @keydown="onKeydown"
    />
    <button class="send" type="submit" :disabled="busy || !text.trim()">Send</button>
  </form>
</template>

<style scoped>
.composer {
  max-width: 720px;
  margin: 0 auto;
  display: flex;
  gap: 10px;
  align-items: flex-end;
}

textarea {
  flex: 1;
  resize: none;
  min-height: 44px;
  max-height: 160px;
  padding: 11px 13px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--bg);
  color: var(--text);
  font: inherit;
}

textarea:focus {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.send {
  height: 44px;
  padding: 0 20px;
  border: 0;
  border-radius: var(--radius);
  background: var(--accent);
  color: #fff;
  font: inherit;
  font-weight: 600;
  cursor: pointer;
}

.send:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>