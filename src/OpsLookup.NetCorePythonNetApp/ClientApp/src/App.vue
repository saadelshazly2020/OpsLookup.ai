<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import ChatComposer from './components/ChatComposer.vue'
import ChatMessage from './components/ChatMessage.vue'
import { fetchHealth, sendChat } from './services/api'
import type { Health, Message } from './types'

const THREAD_KEY = 'opslookup.netcore.threadId'

const EXAMPLES = [
  'What contracts are held for social ID 1234567890?',
  'Can you look up social ID 2111111111 for me?',
  'Check social ID 1234567890, only the network contract',
]

const messages = ref<Message[]>([])
const busy = ref(false)
const status = ref('')
const health = ref<Health | null>(null)
const thread = ref(nextThreadId())
const threadEl = ref<HTMLElement | null>(null)

let nextId = 1

function nextThreadId(): string {
  const existing = localStorage.getItem(THREAD_KEY)
  if (existing) return existing
  const created = `T-${Math.random().toString(36).slice(2, 8).toUpperCase()}`
  localStorage.setItem(THREAD_KEY, created)
  return created
}

function scrollToEnd() {
  nextTick(() => {
    if (threadEl.value) threadEl.value.scrollTop = threadEl.value.scrollHeight
  })
}

function push(message: Omit<Message, 'id'>) {
  messages.value.push({ id: nextId++, ...message })
  scrollToEnd()
}

async function send(text: string) {
  if (busy.value) return

  push({ role: 'member', text, toolCalls: 0, isError: false })
  busy.value = true
  status.value = ''

  try {
    const result = await sendChat(thread.value, text)
    push({
      role: 'agent',
      text: result.reply,
      toolCalls: result.toolCalls,
      isError: false,
    })
    // An answer with no badge was not checked against the database. Saying so
    // matters: silence here reads as "verified" when it is not.
    status.value =
      result.toolCalls > 0
        ? 'Answered from a database lookup.'
        : 'Answered without a database lookup.'
  } catch (error) {
    push({
      role: 'agent',
      text: error instanceof Error ? error.message : 'Something went wrong.',
      toolCalls: 0,
      isError: true,
    })
  } finally {
    busy.value = false
  }
}

function startNewConversation() {
  localStorage.removeItem(THREAD_KEY)
  thread.value = nextThreadId()
  messages.value = []
  status.value = ''
}

const isEmpty = computed(() => messages.value.length === 0)

onMounted(async () => {
  health.value = await fetchHealth()
})
</script>

<template>
  <header>
    <h1>OpsLookup Agent</h1>
    <span class="sub">Ask about a party's contracts in plain language.</span>
    <span v-if="health && !health.pythonReady" class="pill warn">Python starting…</span>
    <span v-else-if="health && !health.apiReachable" class="pill warn">Data API unreachable</span>
  </header>

  <main ref="threadEl" role="log" aria-live="polite" aria-label="Conversation">
    <section v-if="isEmpty" class="empty">
      <h2>Start a lookup</h2>
      <p>Describe the ticket in your own words. Include the social ID if you have it.</p>
      <div class="examples">
        <button
          v-for="example in EXAMPLES"
          :key="example"
          type="button"
          @click="send(example)"
        >
          {{ example }}
        </button>
      </div>
    </section>

    <ChatMessage
      v-for="message in messages"
      :key="message.id"
      :message="message"
    />

    <div v-if="busy" class="thinking">
      <div class="who">Agent</div>
      <div class="bubble dots"><span /><span /><span /></div>
    </div>
  </main>

  <footer>
    <ChatComposer :busy="busy" @send="send" />
    <div class="meta">
      <span>Ticket {{ thread }}</span>
      <span>{{ status }}</span>
      <button type="button" class="link" @click="startNewConversation">New conversation</button>
    </div>
  </footer>
</template>

<style scoped>
header {
  background: var(--surface);
  border-bottom: 1px solid var(--border);
  padding: 12px 20px;
  display: flex;
  align-items: baseline;
  gap: 12px;
  flex-wrap: wrap;
}

h1 {
  font-size: 16px;
  margin: 0;
  font-weight: 650;
}

.sub {
  color: var(--muted);
  font-size: 13px;
}

.pill {
  font-size: 12px;
  border-radius: 999px;
  padding: 2px 10px;
}

.pill.warn {
  background: var(--error-soft);
  color: var(--error);
}

main {
  flex: 1;
  overflow-y: auto;
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.empty {
  max-width: 720px;
  margin: 24px auto 0;
  color: var(--muted);
}

.empty h2 {
  font-size: 17px;
  color: var(--text);
  margin: 0 0 6px;
}

.empty p {
  margin: 0 0 14px;
  font-size: 14px;
}

.examples {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.examples button {
  text-align: left;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 10px 13px;
  font: inherit;
  font-size: 14px;
  color: var(--text);
  cursor: pointer;
}

.examples button:hover {
  border-color: var(--accent);
  background: var(--accent-soft);
}

.thinking {
  max-width: 720px;
  width: 100%;
  margin: 0 auto;
}

.thinking .who {
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--muted);
  margin-bottom: 5px;
}

.thinking .bubble {
  background: var(--agent-bubble);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 11px 14px;
}

.dots span {
  display: inline-block;
  width: 6px;
  height: 6px;
  margin-right: 3px;
  border-radius: 50%;
  background: var(--muted);
  animation: blink 1.2s infinite;
}

.dots span:nth-child(2) {
  animation-delay: 0.2s;
}

.dots span:nth-child(3) {
  animation-delay: 0.4s;
}

@keyframes blink {
  0%,
  60%,
  100% {
    opacity: 0.25;
  }
  30% {
    opacity: 1;
  }
}

footer {
  background: var(--surface);
  border-top: 1px solid var(--border);
  padding: 12px 20px;
}

.meta {
  max-width: 720px;
  margin: 8px auto 0;
  font-size: 12px;
  color: var(--muted);
  display: flex;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.link {
  background: none;
  border: 0;
  color: var(--muted);
  text-decoration: underline;
  font: inherit;
  font-size: 12px;
  cursor: pointer;
  padding: 0;
}

@media (max-width: 600px) {
  main,
  footer {
    padding: 12px;
  }
}
</style>