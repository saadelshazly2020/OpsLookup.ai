<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import AppIcon from './components/AppIcon.vue'
import ChatComposer from './components/ChatComposer.vue'
import ChatMessage from './components/ChatMessage.vue'
import { useI18n } from './composables/useI18n'
import { useTheme } from './composables/useTheme'
import { fetchHealth, sendChat } from './services/api'
import type { Health, Message } from './types'

const THREAD_KEY = 'opslookup.netcore.threadId'

const { t, locale, locales, localeInfo } = useI18n()
const { choice: theme } = useTheme()

/**
 * Example prompts. Defined as a computed rather than a constant so they follow
 * the active language — a member who switched to Arabic should not be shown
 * English starter questions.
 */
const EXAMPLES = computed(() => [
  { label: t.value.example1Label, text: t.value.example1Text },
  { label: t.value.example2Label, text: t.value.example2Text },
  { label: t.value.example3Label, text: t.value.example3Text },
])

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
    // The locale travels with the request so the agent answers in the same
    // language the member is reading.
    const result = await sendChat(thread.value, text, locale.value)
    push({
      role: 'agent',
      text: result.reply,
      toolCalls: result.toolCalls,
      lookupFailed: result.lookupFailed ?? false,
      isError: false,
    })
    // An answer with no badge was not checked against the database. Saying so
    // matters: silence here reads as "verified" when it is not.
    status.value = result.lookupFailed
      ? t.value.statusFailed
      : result.toolCalls > 0
        ? t.value.statusVerified
        : t.value.statusUnverified
  } catch (error) {
    push({
      role: 'agent',
      text: error instanceof Error ? error.message : t.value.somethingWentWrong,
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

/**
 * Drives the health pill. Returning one value rather than three booleans keeps
 * the template from re-deriving the same priority order.
 */
const healthPill = computed(() => {
  if (!health.value) return null
  if (!health.value.pythonReady) {
    return { tone: 'warn', label: t.value.starting, hint: health.value.startupError ?? undefined }
  }
  if (!health.value.apiReachable) {
    return { tone: 'warn', label: t.value.apiUnreachable, hint: t.value.apiUnreachableHint }
  }
  return { tone: 'ok', label: t.value.ready, hint: undefined }
})

const toneClass: Record<string, string> = {
  ok: 'bg-ok-soft text-ok',
  warn: 'bg-[#fef3e2] text-[#b54708] dark:bg-[#2e2413] dark:text-[#f5b74e]',
}

const THEME_OPTIONS = [
  { value: 'light', labelKey: 'themeLight', icon: 'sun' },
  { value: 'system', labelKey: 'themeSystem', icon: 'monitor' },
  { value: 'dark', labelKey: 'themeDark', icon: 'moon' },
] as const

onMounted(async () => {
  health.value = await fetchHealth()
})
</script>

<template>
  <div class="flex h-full flex-col bg-canvas">
    <header class="z-10 border-b border-line bg-surface/85 backdrop-blur-md">
      <div class="mx-auto flex w-full max-w-5xl items-center gap-3 px-4 py-3 sm:px-6">
        <div class="flex size-9 shrink-0 items-center justify-center rounded-xl bg-accent text-white shadow-sm">
          <AppIcon name="shield" class="size-5" />
        </div>

        <div class="min-w-0 flex-1">
          <h1 class="truncate text-[0.9375rem] font-semibold leading-tight tracking-tight">
            {{ t.appName }}
          </h1>
          <p class="truncate text-xs text-muted">{{ t.tagline }}</p>
        </div>

        <div
          v-if="healthPill"
          class="flex shrink-0 items-center gap-1.5 rounded-full px-2.5 py-1 text-[0.6875rem] font-medium"
          :class="toneClass[healthPill.tone]"
          :title="healthPill.hint"
        >
          <span
            class="size-1.5 rounded-full"
            :class="healthPill.tone === 'ok' ? 'bg-ok' : 'animate-pulse bg-current'"
          />
          <span class="hidden sm:inline">{{ healthPill.label }}</span>
        </div>

        <!--
          Language switch. Arabic first, because it is the default. Also flips
          `dir` on <html>, which is what mirrors the whole layout.
        -->
        <div
          class="flex shrink-0 items-center rounded-lg border border-line bg-surface p-0.5"
          role="group"
          :aria-label="t.themeLabel"
        >
          <button
            v-for="option in locales"
            :key="option"
            type="button"
            class="rounded-md px-2 py-1 text-xs font-medium transition-colors"
            :class="
              locale === option ? 'bg-accent-soft text-accent' : 'text-muted hover:bg-canvas hover:text-ink'
            "
            :aria-pressed="locale === option"
            :title="localeInfo[option].label"
            @click="locale = option"
          >
            {{ localeInfo[option].label }}
          </button>
        </div>

        <div
          class="hidden shrink-0 items-center rounded-lg border border-line bg-surface p-0.5 sm:flex"
          role="group"
          :aria-label="t.themeLabel"
        >
          <button
            v-for="option in THEME_OPTIONS"
            :key="option.value"
            type="button"
            class="rounded-md p-1.5 transition-colors"
            :class="
              theme === option.value
                ? 'bg-accent-soft text-accent'
                : 'text-muted hover:bg-canvas hover:text-ink'
            "
            :aria-pressed="theme === option.value"
            :title="t[option.labelKey]"
            @click="theme = option.value"
          >
            <AppIcon :name="option.icon" class="size-4" />
            <span class="sr-only">{{ t[option.labelKey] }}</span>
          </button>
        </div>
      </div>
    </header>

    <main
      ref="threadEl"
      role="log"
      aria-live="polite"
      :aria-label="t.conversationLabel"
      class="flex-1 overflow-y-auto px-4 py-6 sm:px-6"
    >
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-6">
        <section v-if="isEmpty" class="pt-6 text-center">
          <div class="mx-auto mb-4 flex size-14 items-center justify-center rounded-2xl bg-accent-soft text-accent">
            <AppIcon name="sparkle" class="size-7" />
          </div>

          <h2 class="text-xl font-semibold tracking-tight">{{ t.startTitle }}</h2>
          <p class="mx-auto mt-2 max-w-md text-sm leading-relaxed text-muted">
            {{ t.startBody }}
          </p>

          <div class="mx-auto mt-6 grid max-w-xl gap-2 sm:grid-cols-1">
            <button
              v-for="example in EXAMPLES"
              :key="example.text"
              type="button"
              class="group flex items-center gap-3 rounded-xl border border-line bg-surface px-4 py-3 text-start shadow-sm transition-all hover:-translate-y-px hover:border-accent hover:shadow-md focus-visible:outline-offset-2"
              @click="send(example.text)"
            >
              <span class="flex-1">
                <span class="block text-[0.8125rem] font-medium">{{ example.label }}</span>
                <span class="mt-0.5 block text-xs leading-relaxed text-muted">{{ example.text }}</span>
              </span>
              <AppIcon
                name="send"
                class="rtl-flip size-4 shrink-0 text-muted transition-colors group-hover:text-accent"
              />
            </button>
          </div>
        </section>

        <ChatMessage v-for="message in messages" :key="message.id" :message="message" />

        <div v-if="busy" class="flex flex-col items-start pe-8 sm:pe-24">
          <div class="mb-1.5 flex items-center gap-1.5 text-[0.6875rem] font-medium tracking-wide text-muted">
            <AppIcon name="sparkle" class="size-3.5" />
            <span>{{ t.agent }}</span>
          </div>
          <div class="flex items-center gap-1.5 rounded-2xl rounded-bl-md border border-line bg-agent-bubble px-4 py-4 shadow-sm">
            <span
              v-for="n in 3"
              :key="n"
              class="size-1.5 animate-bounce rounded-full bg-muted"
              :style="{ animationDelay: `${(n - 1) * 0.16}s` }"
            />
          </div>
        </div>
      </div>
    </main>

    <footer class="border-t border-line bg-surface/85 backdrop-blur-md">
      <div class="mx-auto w-full max-w-3xl px-4 py-3 sm:px-6">
        <ChatComposer :busy="busy" @send="send" />

        <div class="mt-2.5 flex flex-wrap items-center justify-between gap-2 px-1 text-[0.6875rem] text-muted">
          <span class="flex items-center gap-1.5">
            <span class="font-medium">{{ t.ticket }} {{ thread }}</span>
            <span v-if="status" class="before:me-1.5 before:text-line before:content-['·']">
              {{ status }}
            </span>
          </span>

          <button
            type="button"
            class="flex items-center gap-1.5 rounded-md px-1.5 py-1 font-medium transition-colors hover:text-accent"
            @click="startNewConversation"
          >
            <AppIcon name="plus" class="size-3.5" />
            {{ t.newConversation }}
          </button>
        </div>
      </div>
    </footer>
  </div>
</template>