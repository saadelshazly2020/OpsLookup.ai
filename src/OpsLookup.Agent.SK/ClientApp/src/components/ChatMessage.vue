<script setup lang="ts">
import { computed } from 'vue'
import AppIcon from './AppIcon.vue'
import { useI18n } from '../composables/useI18n'
import { renderMarkdown } from '../services/markdown'
import type { Message } from '../types'

const props = defineProps<{ message: Message }>()

const { t, number } = useI18n()

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
    return { tone: 'fail', icon: 'alert' as const, text: t.value.badgeFailed, title: t.value.badgeFailedTitle }
  }

  if (props.message.toolCalls > 0) {
    return {
      tone: 'ok',
      icon: 'database' as const,
      // Arabic has singular and plural, so the two forms are separate strings
      // rather than the "time{{n === 1 ? '' : 's'}}" trick used in English.
      text:
        props.message.toolCalls === 1
          ? t.value.badgeOkOnce
          : t.value.badgeOk.replace('{n}', number(props.message.toolCalls)),
      title: t.value.badgeOkTitle,
    }
  }

  return { tone: 'none', icon: 'alert' as const, text: t.value.badgeNone, title: t.value.badgeNoneTitle }
})

const badgeClass: Record<string, string> = {
  ok: 'bg-ok-soft text-ok',
  fail: 'bg-danger-soft text-danger',
  none: 'bg-[#fef3e2] text-[#b54708] dark:bg-[#2e2413] dark:text-[#f5b74e]',
}

const speaker = computed(() =>
  props.message.role === 'member'
    ? t.value.you
    : props.message.isError
      ? t.value.problem
      : t.value.agent,
)
</script>

<template>
  <div
    class="flex w-full flex-col"
    :class="
      message.role === 'member' ? 'items-end ps-8 sm:ps-24' : 'items-start pe-8 sm:pe-24'
    "
  >
    <!-- Logical `ms-*`, so the icon sits on the correct side in both directions. -->
    <div class="mb-1.5 flex items-center gap-1.5 text-[0.6875rem] font-medium tracking-wide text-muted">
      <AppIcon :name="isAgent ? 'sparkle' : 'user'" class="size-3.5 shrink-0" />
      <span>{{ speaker }}</span>
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
      <div class="prose-agent px-4" v-html="html" />
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