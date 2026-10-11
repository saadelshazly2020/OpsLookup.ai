<script setup lang="ts">
import { computed } from 'vue'
import AppIcon from './AppIcon.vue'
import { renderMarkdown } from '../services/markdown'
import type { Message } from '../types'

const props = defineProps<{ message: Message }>()

const isAgent = computed(() => props.message.role === 'agent')

// Sanitised in renderMarkdown. Never bind message.text to v-html directly.
const html = computed(() => (isAgent.value ? renderMarkdown(props.message.text) : ''))

/*
  Three states, not two. `toolCalls` counts invocations rather than successes,
  so a lookup that ran and failed also reports 1 — badging that green would be
  a lie. Failed reads red, unattempted reads amber, and only a successful read
  reads green.
*/
const badge = computed(() => {
  if (props.message.lookupFailed) {
    return {
      tone: 'fail',
      icon: 'alert' as const,
      text: 'Lookup failed — not verified',
      title: 'A tool ran but could not reach the data. Nothing was read from the database.',
    }
  }

  if (props.message.toolCalls > 0) {
    const n = props.message.toolCalls
    return {
      tone: 'ok',
      icon: 'database' as const,
      text: `Looked up ${n} time${n === 1 ? '' : 's'} in the database`,
      title: 'This answer was read from the database.',
    }
  }

  return {
    tone: 'none',
    icon: 'alert' as const,
    text: 'Not checked against the database',
    title: 'No database lookup was made, so this answer was not verified.',
  }
})

const badgeClass: Record<string, string> = {
  ok: 'bg-ok-soft text-ok',
  fail: 'bg-danger-soft text-danger',
  none: 'bg-[#fef3e2] text-[#b54708] dark:bg-[#2e2413] dark:text-[#f5b74e]',
}
</script>

<template>
  <div
    class="flex w-full flex-col"
    :class="[
      message.role === 'member' ? 'items-end' : 'items-start',
      message.role === 'member' ? 'pl-8 sm:pl-24' : 'pr-8 sm:pr-24',
    ]"
  >
    <div class="mb-1.5 flex items-center gap-1.5 text-[0.6875rem] font-medium tracking-wide text-muted">
      <AppIcon
        :name="isAgent ? 'sparkle' : 'user'"
        class="size-3.5"
      />
      <span>
        {{ message.role === 'member' ? 'You' : message.isError ? 'Problem' : 'OpsLookup' }}
      </span>
    </div>

    <!--
      Error and member bubbles stay plain text: an error string or a typed
      question has no business being interpreted as markdown.
    -->
    <div
      v-if="!isAgent"
      class="max-w-full whitespace-pre-wrap rounded-2xl rounded-br-md bg-accent px-4 py-2.5 text-[0.9375rem] leading-relaxed text-white shadow-sm dark:text-canvas"
    >
      {{ message.text }}
    </div>

    <div
      v-else-if="message.isError"
      class="max-w-full rounded-2xl rounded-bl-md border border-danger-line bg-danger-soft px-4 py-3 text-[0.9375rem] leading-relaxed text-danger"
      role="alert"
    >
      {{ message.text }}
    </div>

    <div
      v-else
      class="max-w-full rounded-2xl rounded-bl-md border border-line bg-agent-bubble px-4 py-3 text-ink shadow-sm ring-1 ring-black/[0.02]"
    >
      <div class="prose-agent" v-html="html" />
    </div>

    <!--
      The badge is the point of the whole app. Without it a member cannot tell
      an answer read from SQL Server from one the model invented, so an answer
      with no badge was NOT verified against data.
    -->
    <div
      v-if="isAgent && !message.isError"
      class="mt-2 flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[0.6875rem] font-medium"
      :class="badgeClass[badge.tone]"
      :title="badge.title"
    >
      <AppIcon :name="badge.icon" class="size-3.5 shrink-0" />
      <span>{{ badge.text }}</span>
    </div>
  </div>
</template>